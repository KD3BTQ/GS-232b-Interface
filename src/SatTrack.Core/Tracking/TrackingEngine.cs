using SatTrack.Core.Catalog;
using SatTrack.Core.Geo;
using SatTrack.Core.Orbit;
using SatTrack.Core.Rotator;
using SatTrack.Core.Time;

namespace SatTrack.Core.Tracking;

public enum RotatorConnection { Disconnected, Connecting, Connected, Lost }

/// <summary>Tuning for how the engine drives the rotator.</summary>
public sealed record TrackingOptions(
    double CommandThresholdDeg = 2.0,
    double LeadSeconds = 2.0,
    double PrePositionMinutes = 2.0,
    bool ParkAfterPass = false,
    double ParkAzimuth = 180,
    double ParkElevation = 0);

/// <summary>Everything the UI needs to draw one frame. Immutable.</summary>
public sealed record TrackerSnapshot
{
    public DateTime UtcNow { get; init; }
    public bool Simulating { get; init; }
    public double ClockRate { get; init; } = 1;

    public RotatorConnection Connection { get; init; }
    public string? RotatorDescription { get; init; }
    public bool Armed { get; init; }
    public bool Tracking { get; init; }

    /// <summary>True while a manual slew position is being held.</summary>
    public bool Manual { get; init; }

    public StationLocation? Station { get; init; }
    public RotatorLimits Limits { get; init; } = new();

    public string? SatelliteName { get; init; }
    public ElementSet? Elements { get; init; }
    public string? SatelliteProblem { get; init; }
    public LookAngles? Look { get; init; }
    public SubPoint? SubPoint { get; init; }
    public PassInfo? Pass { get; init; }
    public bool PassUsesFlip { get; init; }
    public bool PassUsesOverlap { get; init; }

    public RotatorPosition? RotatorPosition { get; init; }
    public RotatorPosition? Target { get; init; }
    public bool TargetFlipped { get; init; }

    /// <summary>Short sentence describing what tracking is doing right now.</summary>
    public string Activity { get; init; } = "";

    public IReadOnlyList<GeoPoint> TrackPast { get; init; } = Array.Empty<GeoPoint>();
    public IReadOnlyList<GeoPoint> TrackFuture { get; init; } = Array.Empty<GeoPoint>();
}

/// <summary>
/// Runs the tracking loop on a background task. UI code calls the public methods and reads
/// <see cref="Snapshot"/> on a timer. Rotator I/O is serialised through one gate so that a
/// disarm can never be overtaken by a movement command that was already being prepared.
/// </summary>
public sealed class TrackingEngine : IDisposable
{
    private readonly OrbitalElementsService _elements;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _io = new(1, 1);

    // Configuration (guarded by _gate)
    private IClock _clock = SystemClock.Instance;
    private StationLocation? _station;
    private RotatorLimits _limits = new();
    private TrackingOptions _options = new();
    private SatelliteEntry? _entry;
    private SatellitePredictor? _predictor;
    private string? _satProblem;
    private int _version;

    // Rotator state
    private IRotator? _rotator;
    private RotatorConnection _connection = RotatorConnection.Disconnected;
    private volatile bool _armed;
    private volatile bool _tracking;
    private RotatorPosition? _rotatorPosition;

    // Loop-owned state
    private int _loopVersion = -1;
    private PassInfo? _pass;
    private DateTime _nextPassSearchUtc = DateTime.MinValue;
    private PassPlan? _plan;
    private RotatorPosition? _lastCommand;
    private DateTime _lastCommandReal;
    private DateTime _lastPollReal;
    private int _pollFailures;
    private DateTime _lastLoopUtc;
    private bool _parkPending;
    private bool _wasInPass;
    private RotatorPosition? _manualTarget; // guarded by _gate
    private DateTime _trackComputedUtc = DateTime.MinValue;
    private IReadOnlyList<GeoPoint> _trackPast = Array.Empty<GeoPoint>();
    private IReadOnlyList<GeoPoint> _trackFuture = Array.Empty<GeoPoint>();

    private TrackerSnapshot _snapshot = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>Raised (on a background thread) for things worth showing in the status bar.</summary>
    public event Action<string>? Message;

    public TrackingEngine(OrbitalElementsService elements)
    {
        _elements = elements;
    }

    public TrackerSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public IClock Clock { get { lock (_gate) return _clock; } }

    public bool IsConnected { get { lock (_gate) return _connection == RotatorConnection.Connected; } }

    public bool IsArmed => _armed;

    public bool IsTracking => _tracking;

    // ------------------------------------------------------------------ lifecycle

    public void Start()
    {
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Run(() => LoopAsync(ct));
    }

    public void Dispose()
    {
        _armed = false;
        _cts?.Cancel();
        try { _loop?.Wait(2000); } catch { /* shutting down */ }

        IRotator? r;
        lock (_gate) { r = _rotator; _rotator = null; }
        if (r is not null)
        {
            try { r.Stop(); } catch { /* best effort */ }
            r.Dispose();
        }
        _io.Dispose();
    }

    // ------------------------------------------------------------------ configuration

    public void Configure(StationLocation? station, RotatorLimits limits, TrackingOptions options)
    {
        lock (_gate)
        {
            bool stationChanged = station != _station;
            _station = station;
            _limits = limits;
            _options = options;
            if (stationChanged) RebuildPredictor();
            _version++;
        }
    }

    public void SetClock(IClock clock)
    {
        lock (_gate)
        {
            _clock = clock;
            _version++;
        }
    }

    /// <summary>Selects a satellite. Stops tracking (but leaves the rotator armed state alone).</summary>
    public void SelectSatellite(SatelliteEntry? entry)
    {
        if (_tracking) StopTracking();
        lock (_gate)
        {
            _entry = entry;
            RebuildPredictor();
            _version++;
        }
    }

    /// <summary>Call after orbital elements have been refreshed.</summary>
    public void ReloadElements()
    {
        lock (_gate)
        {
            RebuildPredictor();
            _version++;
        }
    }

    private void RebuildPredictor()
    {
        _predictor = null;
        _satProblem = null;
        if (_entry is null) return;
        if (_station is null)
        {
            _satProblem = "Set your location in Settings to start predicting passes.";
            return;
        }

        var sat = _elements.CreateSatellite(_entry, out var elements, out var reason);
        if (sat is null)
        {
            _satProblem = reason;
            return;
        }
        _predictor = new SatellitePredictor(sat, _station, _entry.Name, elements);
    }

    /// <summary>
    /// The pass in progress at the given time, or the next one. With <paramref name="skipCurrent"/>
    /// a pass already in progress is skipped (used by simulation's "next pass").
    /// </summary>
    public PassInfo? PredictPass(DateTime fromUtc, bool skipCurrent = false)
    {
        SatellitePredictor? p;
        lock (_gate) p = _predictor;
        if (p is null) return null;

        var pass = p.FindPass(fromUtc, TimeSpan.FromDays(2));
        if (pass is not null && skipCurrent && pass.IsInProgress(fromUtc) && !pass.NeverSets)
            pass = p.FindPass(pass.LosUtc + TimeSpan.FromMinutes(1), TimeSpan.FromDays(2));
        return pass;
    }

    // ------------------------------------------------------------------ rotator control

    /// <summary>
    /// Opens the rotator and confirms it answers with a position. Throws with a readable
    /// message if it doesn't.
    /// </summary>
    public async Task<RotatorPosition> ConnectAsync(IRotator rotator)
    {
        await DisconnectAsync().ConfigureAwait(false);

        lock (_gate)
        {
            _rotator = rotator;
            _connection = RotatorConnection.Connecting;
        }

        try
        {
            var pos = await Task.Run(() =>
            {
                rotator.Open();
                Exception? last = null;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try { return rotator.GetPosition(); }
                    catch (Exception ex) { last = ex; Thread.Sleep(200); }
                }
                throw new IOException(
                    $"The port opened but the controller didn't report a position ({last?.Message}). " +
                    "Check the cable, baud rate and that the controller is powered.", last);
            }).ConfigureAwait(false);

            lock (_gate)
            {
                _rotatorPosition = pos;
                _connection = RotatorConnection.Connected;
                _pollFailures = 0;
                _lastCommand = null;
            }
            Raise($"Connected to {rotator.Description}. Rotator at az {pos.Azimuth:0}°, el {pos.Elevation:0}°.");
            return pos;
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _rotator = null;
                _connection = RotatorConnection.Disconnected;
            }
            try { rotator.Dispose(); } catch { /* ignore */ }

            throw ex switch
            {
                UnauthorizedAccessException => new IOException(
                    $"Couldn't open {rotator.Description}: the port is in use by another program.", ex),
                FileNotFoundException or ArgumentException => new IOException(
                    $"Couldn't open {rotator.Description}: that port doesn't exist. Check Settings.", ex),
                _ => ex,
            };
        }
    }

    public async Task DisconnectAsync()
    {
        _armed = false;
        IRotator? r;
        lock (_gate)
        {
            r = _rotator;
            _rotator = null;
            _connection = RotatorConnection.Disconnected;
            _rotatorPosition = null;
            _manualTarget = null;
            _lastCommand = null;
        }
        if (r is null) return;

        await _io.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                try { r.Stop(); } catch { /* port may already be gone */ }
                r.Dispose();
            }).ConfigureAwait(false);
        }
        finally
        {
            _io.Release();
        }
        Raise("Rotator disconnected.");
    }

    /// <summary>Allows movement commands. Returns false if no rotator is connected.</summary>
    public bool Arm()
    {
        lock (_gate)
        {
            if (_connection != RotatorConnection.Connected) return false;
            _lastCommand = null;
        }
        _armed = true;
        Raise("Rotator enabled. Movement commands will be sent.");
        return true;
    }

    /// <summary>Stops sending commands and tells the rotator to stop. Safe to call any time.</summary>
    public void Disarm(string? reason = null)
    {
        bool wasArmed = _armed;
        _armed = false;

        IRotator? r;
        lock (_gate)
        {
            r = _rotator;
            _lastCommand = null;
            _manualTarget = null;
        }

        if (r is not null)
        {
            _ = Task.Run(async () =>
            {
                await _io.WaitAsync().ConfigureAwait(false);
                try { r.Stop(); }
                catch (Exception ex) { Raise($"Stop command failed: {ex.Message}"); }
                finally { _io.Release(); }
            });
        }

        if (wasArmed || reason is not null)
            Raise(reason ?? "Rotator disarmed and stopped.");
    }

    public void StartTracking()
    {
        lock (_gate)
        {
            _lastCommand = null;
            _manualTarget = null;
        }
        _parkPending = false;
        _tracking = true;
        Raise(_armed ? "Tracking started." : "Tracking started. Press Enable to let the rotator move.");
    }

    public void StopTracking()
    {
        _tracking = false;
        if (_armed)
        {
            // Stay armed, but stop any motion in progress.
            IRotator? r;
            lock (_gate) { r = _rotator; _lastCommand = null; }
            if (r is not null)
            {
                _ = Task.Run(async () =>
                {
                    await _io.WaitAsync().ConfigureAwait(false);
                    try { r.Stop(); } catch { /* reported by polling */ }
                    finally { _io.Release(); }
                });
            }
        }
        Raise("Tracking stopped.");
    }

    /// <summary>
    /// Sends the rotator to a fixed position (rotator coordinates; azimuth may be in the
    /// 360-450° overlap). Stops tracking. Requires a connected, enabled rotator.
    /// </summary>
    public bool ManualGoTo(double azimuth, double elevation, out string? error)
    {
        error = null;
        RotatorLimits limits;
        lock (_gate)
        {
            if (_connection != RotatorConnection.Connected) { error = "Connect to the rotator first."; return false; }
            limits = _limits;
        }
        if (!_armed) { error = "Press Enable first. The rotator only moves when it's enabled."; return false; }

        double az = Math.Clamp(azimuth, 0, limits.MaxAzimuth);
        double el = Math.Clamp(elevation, 0, limits.MaxElevation);

        bool wasTracking = _tracking;
        _tracking = false; // no stop command: the new position supersedes the old one
        lock (_gate)
        {
            _manualTarget = new RotatorPosition(az, el);
            _lastCommand = null; // the loop sends it on its next pass (within ~0.1 s)
        }
        Raise((wasTracking ? "Tracking stopped. " : "") + $"Manual slew to az {az:0}°, el {el:0}°.");
        return true;
    }

    /// <summary>Stops motion and cancels any manual slew, but stays enabled.</summary>
    public void StopMotion()
    {
        IRotator? r;
        lock (_gate)
        {
            r = _rotator;
            _manualTarget = null;
            _lastCommand = null;
        }
        if (r is null) return;
        _tracking = false;
        _ = Task.Run(async () =>
        {
            await _io.WaitAsync().ConfigureAwait(false);
            try { r.Stop(); }
            catch (Exception ex) { Raise($"Stop command failed: {ex.Message}"); }
            finally { _io.Release(); }
        });
        Raise("Rotator stopped.");
    }

    // ------------------------------------------------------------------ loop

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await StepAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Raise($"Tracking error: {ex.Message}");
            }

            try { await Task.Delay(100, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task StepAsync()
    {
        IClock clock;
        StationLocation? station;
        RotatorLimits limits;
        TrackingOptions opts;
        SatellitePredictor? predictor;
        IRotator? rotator;
        RotatorConnection connection;
        string? satProblem;
        int version;

        lock (_gate)
        {
            clock = _clock;
            station = _station;
            limits = _limits;
            opts = _options;
            predictor = _predictor;
            rotator = _rotator;
            connection = _connection;
            satProblem = _satProblem;
            version = _version;
        }

        var now = clock.UtcNow;
        var realNow = DateTime.UtcNow;

        // Reset derived state when configuration changes or the clock jumps.
        bool clockJumped = _lastLoopUtc != default &&
                           (now < _lastLoopUtc - TimeSpan.FromSeconds(1) || now - _lastLoopUtc > TimeSpan.FromMinutes(2));
        if (version != _loopVersion || clockJumped)
        {
            _loopVersion = version;
            _pass = null;
            _plan = null;
            _nextPassSearchUtc = DateTime.MinValue;
            _trackComputedUtc = DateTime.MinValue;
            _wasInPass = false;
            if (clockJumped) lock (_gate) _lastCommand = null;
        }
        _lastLoopUtc = now;

        // ---- satellite position
        LookAngles? look = predictor?.Observe(now);
        SubPoint? sub = predictor?.GetSubPoint(now);

        // ---- pass prediction
        if (predictor is not null)
        {
            bool passOver = _pass is not null && now > _pass.LosUtc;
            if ((_pass is null && now >= _nextPassSearchUtc) || passOver)
            {
                if (passOver && _wasInPass && _tracking && opts.ParkAfterPass) _parkPending = true;
                _wasInPass = false;
                _pass = predictor.FindPass(now, TimeSpan.FromDays(2));
                _plan = null;
                if (_pass is null) _nextPassSearchUtc = now + TimeSpan.FromMinutes(30);
            }
        }
        else
        {
            _pass = null;
            _plan = null;
        }

        // ---- ground track
        if (predictor is not null && (now - _trackComputedUtc).Duration() > TimeSpan.FromSeconds(20))
        {
            var period = predictor.Period;
            var stepT = TimeSpan.FromSeconds(Math.Clamp(period.TotalSeconds / 200, 10, 600));
            _trackPast = predictor.GroundTrack(now - TimeSpan.FromTicks(period.Ticks / 4), now, stepT);
            _trackFuture = predictor.GroundTrack(now, now + period, stepT);
            _trackComputedUtc = now;
        }
        else if (predictor is null)
        {
            _trackPast = _trackFuture = Array.Empty<GeoPoint>();
        }

        // ---- poll rotator position
        if (rotator is not null && connection == RotatorConnection.Connected)
        {
            var interval = rotator is SimulatedRotator ? TimeSpan.FromMilliseconds(200) : TimeSpan.FromSeconds(1);
            if (realNow - _lastPollReal >= interval)
            {
                _lastPollReal = realNow;
                await _io.WaitAsync().ConfigureAwait(false);
                try
                {
                    var pos = rotator.GetPosition();
                    lock (_gate) _rotatorPosition = pos;
                    _pollFailures = 0;
                }
                catch (Exception ex)
                {
                    _pollFailures++;
                    if (_pollFailures >= 3)
                    {
                        lock (_gate)
                        {
                            if (_rotator == rotator) _connection = RotatorConnection.Lost;
                        }
                        connection = RotatorConnection.Lost;
                        _armed = false;
                        Raise($"Lost contact with the rotator ({ex.Message}). Disarmed. Reconnect to continue.");
                    }
                }
                finally
                {
                    _io.Release();
                }
            }
        }

        RotatorPosition? rotPos;
        RotatorPosition? manual;
        lock (_gate)
        {
            rotPos = _rotatorPosition;
            manual = _manualTarget;
        }

        // ---- decide where the rotator should point
        RotatorPosition? target = null;
        bool flipped = false;
        string activity;

        if (manual is RotatorPosition mt && !_tracking)
        {
            target = mt;
            bool there = rotPos is RotatorPosition rp
                         && Math.Abs(rp.Azimuth - mt.Azimuth) <= 1.5 && Math.Abs(rp.Elevation - mt.Elevation) <= 1.5;
            activity = there
                ? $"Manual: holding az {mt.Azimuth:0}°, el {mt.Elevation:0}°."
                : $"Manual: slewing to az {mt.Azimuth:0}°, el {mt.Elevation:0}°.";
            if (!_armed) activity = "Manual position set. Press Enable to move.";
        }
        else if (station is null)
        {
            activity = "Set your location in Settings.";
        }
        else if (predictor is null)
        {
            activity = satProblem ?? "Choose a satellite.";
        }
        else if (!_tracking)
        {
            activity = _pass is null ? "No pass in the next 2 days."
                     : _pass.IsInProgress(now) ? $"{predictor.Name} is up. Press Track to follow it."
                     : $"Next pass in {FormatSpan(_pass.AosUtc - now)}.";
        }
        else if (_pass is null)
        {
            activity = "Tracking: no pass in the next 2 days.";
        }
        else
        {
            var preStart = _pass.AosUtc - TimeSpan.FromMinutes(opts.PrePositionMinutes);
            if (now >= preStart && now <= _pass.LosUtc)
            {
                _parkPending = false;
                if (_plan is null || !_plan.Covers(now))
                    _plan = BuildPlan(predictor, _pass, now, limits, rotPos);

                if (now < _pass.AosUtc)
                {
                    target = _plan.StartPosition;
                    flipped = _plan.Points[0].Flipped;
                    activity = $"Waiting at the rise point. Pass starts in {FormatSpan(_pass.AosUtc - now)}.";
                }
                else
                {
                    _wasInPass = true;
                    var aim = now + TimeSpan.FromSeconds(opts.LeadSeconds);
                    if (aim > _pass.LosUtc) aim = _pass.LosUtc;
                    var aimLook = predictor.Observe(aim) ?? look;
                    if (aimLook is LookAngles al)
                    {
                        target = _plan.Map(aim, al.Azimuth, al.Elevation);
                        int idx = Math.Clamp((int)Math.Round((aim - _plan.StartUtc).TotalSeconds / _plan.Step.TotalSeconds),
                                             0, _plan.Points.Count - 1);
                        flipped = _plan.Points[idx].Flipped;
                    }
                    activity = _pass.NeverSets
                        ? $"Following {predictor.Name}."
                        : $"Following {predictor.Name}. Pass ends in {FormatSpan(_pass.LosUtc - now)}.";
                }
            }
            else if (_parkPending)
            {
                target = new RotatorPosition(opts.ParkAzimuth, opts.ParkElevation);
                activity = $"Parked. Next pass in {FormatSpan(_pass.AosUtc - now)}.";
            }
            else
            {
                activity = $"Tracking: next pass in {FormatSpan(_pass.AosUtc - now)}. Holding position.";
            }
        }

        // ---- send movement command
        if (target is RotatorPosition tgt && _armed && rotator is not null && connection == RotatorConnection.Connected)
        {
            RotatorPosition? last;
            lock (_gate) last = _lastCommand;

            bool moved = last is null || Distance(tgt, last.Value) >= opts.CommandThresholdDeg;
            bool resend = last is not null && rotPos is not null
                          && realNow - _lastCommandReal > TimeSpan.FromSeconds(15)
                          && Distance(tgt, rotPos.Value) >= opts.CommandThresholdDeg * 2;

            if (moved || resend)
            {
                await _io.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (_armed) // re-check inside the gate: a disarm may have happened meanwhile
                    {
                        rotator.GoTo(tgt.Azimuth, tgt.Elevation);
                        lock (_gate) _lastCommand = tgt;
                        _lastCommandReal = realNow;
                    }
                }
                catch (Exception ex)
                {
                    Raise($"Move command failed: {ex.Message}");
                }
                finally
                {
                    _io.Release();
                }
            }
        }

        // ---- publish
        bool armed = _armed;
        string? description;
        lock (_gate) { description = _rotator?.Description; connection = _connection; }

        Volatile.Write(ref _snapshot, new TrackerSnapshot
        {
            UtcNow = now,
            Simulating = clock is SimulationClock,
            ClockRate = clock.Rate,
            Connection = connection,
            RotatorDescription = description,
            Armed = armed,
            Tracking = _tracking,
            Manual = manual is not null && !_tracking,
            Station = station,
            Limits = limits,
            SatelliteName = predictor?.Name,
            Elements = predictor?.Elements,
            SatelliteProblem = satProblem,
            Look = look,
            SubPoint = sub,
            Pass = _pass,
            PassUsesFlip = _plan?.UsesFlip ?? false,
            PassUsesOverlap = _plan?.UsesOverlap ?? false,
            RotatorPosition = rotPos,
            Target = target,
            TargetFlipped = flipped,
            Activity = activity,
            TrackPast = _trackPast,
            TrackFuture = _trackFuture,
        });
    }

    private static PassPlan BuildPlan(SatellitePredictor predictor, PassInfo pass, DateTime now,
                                      RotatorLimits limits, RotatorPosition? rotPos)
    {
        // Plan from the rise (or from now if the pass is already under way) to the set,
        // in chunks of at most 3 hours for objects that stay up.
        var start = now < pass.AosUtc ? pass.AosUtc : now;
        var end = pass.LosUtc;
        if (end - start > TimeSpan.FromHours(3)) end = start + TimeSpan.FromHours(3);
        if (end <= start) end = start + TimeSpan.FromSeconds(10);

        var step = TimeSpan.FromSeconds(Math.Clamp((end - start).TotalSeconds / 600, 2, 60));
        var samples = new List<(DateTime, double, double)>();
        for (var t = start; t <= end + step; t += step)
        {
            var l = predictor.Observe(t);
            if (l is LookAngles la) samples.Add((t, la.Azimuth, la.Elevation));
        }
        if (samples.Count == 0)
            samples.Add((start, pass.AosAzimuth, 0));

        return PassPlanner.Build(samples, step, limits, rotPos);
    }

    private static double Distance(RotatorPosition a, RotatorPosition b) =>
        Math.Max(Math.Abs(a.Azimuth - b.Azimuth), Math.Abs(a.Elevation - b.Elevation));

    public static string FormatSpan(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalHours >= 24) return $"{(int)span.TotalDays}d {span.Hours}h";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes:00}m";
        return $"{span.Minutes}:{span.Seconds:00}";
    }

    private void Raise(string message)
    {
        try { Message?.Invoke(message); } catch { /* UI handler problems shouldn't stop tracking */ }
    }
}
