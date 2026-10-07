using SatTrack.Core.Tracking;

namespace SatTrack.Core.Radio;

/// <summary>Which corrected frequency is sent to the radio.</summary>
public enum RadioFollows { Downlink, Uplink }

/// <summary>Everything the frequency panel needs for one frame. Immutable.</summary>
public sealed record RadioSnapshot
{
    public RotatorConnection Connection { get; init; }
    public string? Description { get; init; }
    public bool Armed { get; init; }
    public bool Tracking { get; init; }
    public RadioFollows Follows { get; init; }

    /// <summary>Nominal satellite downlink (what it transmits), Hz.</summary>
    public long? NominalDownlinkHz { get; init; }

    /// <summary>Uplink minus downlink, Hz (0 when the satellite isn't a repeater).</summary>
    public long OffsetHz { get; init; }

    public long? NominalUplinkHz => NominalDownlinkHz is long d && OffsetHz != 0 ? d + OffsetHz : null;

    /// <summary>Downlink as heard on the ground right now.</summary>
    public double? DownlinkHz { get; init; }

    /// <summary>Frequency to transmit right now so the satellite hears the nominal uplink.</summary>
    public double? UplinkHz { get; init; }

    public double? DownlinkShiftHz => DownlinkHz is double d && NominalDownlinkHz is long n ? d - n : null;
    public double? UplinkShiftHz => UplinkHz is double u && NominalUplinkHz is long n ? u - n : null;

    public double? RangeRateKmS { get; init; }

    /// <summary>Last frequency read back from the radio.</summary>
    public long? RadioHz { get; init; }

    /// <summary>Last frequency this app sent.</summary>
    public long? CommandedHz { get; init; }

    public string Activity { get; init; } = "";
}

/// <summary>
/// Keeps the radio on the Doppler-corrected frequency. Same Connect / Enable / Track model as the
/// rotator: nothing is sent to the radio until it is enabled, and only while tracking.
/// Runs its own background loop and reads the satellite geometry from the tracking engine.
/// </summary>
public sealed class RadioController : IDisposable
{
    private readonly Func<TrackerSnapshot> _tracker;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _io = new(1, 1);

    private IRadio? _radio;
    private RotatorConnection _connection = RotatorConnection.Disconnected;
    private volatile bool _armed;
    private volatile bool _tracking;
    private long? _nominalDownHz;
    private long _offsetHz;
    private RadioFollows _follows = RadioFollows.Downlink;
    private long _stepHz = 10;

    private long? _radioHz;
    private long? _lastCommandHz;
    private DateTime _lastCommandReal;
    private DateTime _lastPollReal;
    private int _pollFailures;
    private int _commandFailures;
    private DateTime _backoffUntilReal;

    private RadioSnapshot _snapshot = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public event Action<string>? Message;

    public RadioController(Func<TrackerSnapshot> tracker) => _tracker = tracker;

    public RadioSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public bool IsArmed => _armed;
    public bool IsTracking => _tracking;

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
        try { _loop?.Wait(1500); } catch { /* shutting down */ }
        IRadio? r;
        lock (_gate) { r = _radio; _radio = null; }
        r?.Dispose();
        _io.Dispose();
    }

    // ------------------------------------------------------------------ configuration

    /// <summary>Sets the satellite's nominal downlink and the repeater offset (uplink - downlink).</summary>
    public void SetFrequencies(long? nominalDownlinkHz, long offsetHz)
    {
        lock (_gate)
        {
            _nominalDownHz = nominalDownlinkHz;
            _offsetHz = offsetHz;
            _lastCommandHz = null;
        }
    }

    public void SetOptions(long stepHz, RadioFollows follows)
    {
        lock (_gate)
        {
            _stepHz = Math.Max(1, stepHz);
            if (_follows != follows) _lastCommandHz = null;
            _follows = follows;
        }
    }

    // ------------------------------------------------------------------ connection & arming

    public async Task<long> ConnectAsync(IRadio radio)
    {
        await DisconnectAsync().ConfigureAwait(false);
        lock (_gate)
        {
            _radio = radio;
            _connection = RotatorConnection.Connecting;
        }

        try
        {
            long hz = await Task.Run(() =>
            {
                radio.Open();
                Exception? last = null;
                for (int i = 0; i < 3; i++)
                {
                    try { return radio.GetFrequencyHz(); }
                    catch (Exception ex) { last = ex; Thread.Sleep(150); }
                }
                throw new IOException(
                    $"The port opened but the radio didn't report its frequency ({last?.Message}). " +
                    "For FlexRadio, check SmartSDR CAT is running and the port is a CAT port. " +
                    "For ICOM, check the CI-V address and baud rate match the radio's menu.", last);
            }).ConfigureAwait(false);

            lock (_gate)
            {
                _radioHz = hz;
                _connection = RotatorConnection.Connected;
                _pollFailures = 0;
                _lastCommandHz = null;
            }
            Raise($"Connected to {radio.Description}. Radio on {hz / 1e6:0.000000} MHz.");
            return hz;
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _radio = null;
                _connection = RotatorConnection.Disconnected;
            }
            try { radio.Dispose(); } catch { /* ignore */ }
            throw ex switch
            {
                UnauthorizedAccessException => new IOException($"Couldn't open {radio.Description}: the port is in use by another program.", ex),
                FileNotFoundException or ArgumentException => new IOException($"Couldn't open {radio.Description}: that port doesn't exist. Check Settings.", ex),
                _ => ex,
            };
        }
    }

    public async Task DisconnectAsync()
    {
        _armed = false;
        IRadio? r;
        lock (_gate)
        {
            r = _radio;
            _radio = null;
            _connection = RotatorConnection.Disconnected;
            _radioHz = null;
            _lastCommandHz = null;
        }
        if (r is null) return;
        await _io.WaitAsync().ConfigureAwait(false);
        try { await Task.Run(r.Dispose).ConfigureAwait(false); }
        finally { _io.Release(); }
        Raise("Radio disconnected.");
    }

    public bool Arm()
    {
        lock (_gate)
        {
            if (_connection != RotatorConnection.Connected) return false;
            _lastCommandHz = null;
        }
        _armed = true;
        Raise("Radio enabled. Frequency commands will be sent while tracking.");
        return true;
    }

    public void Disarm()
    {
        bool was = _armed;
        _armed = false;
        if (was) Raise("Radio disarmed. No more frequency commands will be sent.");
    }

    public void StartTracking()
    {
        lock (_gate) _lastCommandHz = null;
        _tracking = true;
        Raise(_armed ? "Radio Doppler tracking started." : "Radio Doppler tracking started. Press Enable to let it tune the radio.");
    }

    public void StopTracking()
    {
        _tracking = false;
        Raise("Radio Doppler tracking stopped.");
    }

    // ------------------------------------------------------------------ loop

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await StepAsync().ConfigureAwait(false); }
            catch (Exception ex) { Raise($"Radio error: {ex.Message}"); }

            try { await Task.Delay(100, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task StepAsync()
    {
        IRadio? radio;
        RotatorConnection connection;
        long? nominal;
        long offset, step;
        RadioFollows follows;
        lock (_gate)
        {
            radio = _radio;
            connection = _connection;
            nominal = _nominalDownHz;
            offset = _offsetHz;
            step = _stepHz;
            follows = _follows;
        }

        var realNow = DateTime.UtcNow;
        var tracker = _tracker();
        double? rr = tracker.Look?.RangeRateKmS;

        double? down = nominal is long n && rr is double r1 ? Doppler.Downlink(n, r1) : null;
        double? up = nominal is long n2 && offset != 0 && rr is double r2 ? Doppler.Uplink(n2 + offset, r2) : null;

        // ---- poll the radio's frequency
        if (radio is not null && connection == RotatorConnection.Connected && realNow - _lastPollReal >= TimeSpan.FromSeconds(1))
        {
            _lastPollReal = realNow;
            await _io.WaitAsync().ConfigureAwait(false);
            try
            {
                long hz = radio.GetFrequencyHz();
                lock (_gate) _radioHz = hz;
                _pollFailures = 0;
            }
            catch (Exception ex)
            {
                if (++_pollFailures >= 3)
                {
                    lock (_gate) { if (_radio == radio) _connection = RotatorConnection.Lost; }
                    connection = RotatorConnection.Lost;
                    _armed = false;
                    Raise($"Lost contact with the radio ({ex.Message}). Disarmed. Reconnect to continue.");
                }
            }
            finally { _io.Release(); }
        }

        // ---- what to send
        double? wanted = follows == RadioFollows.Uplink ? up : down;
        string activity;
        if (nominal is null) activity = "Enter the satellite's downlink frequency.";
        else if (tracker.SatelliteName is null || rr is null) activity = "Choose a satellite with orbital data.";
        else if (follows == RadioFollows.Uplink && up is null) activity = "Radio follows the uplink: enter an offset.";
        else if (!_tracking) activity = "Press Track to keep the radio on the corrected frequency.";
        else if (!_armed) activity = "Tracking. Press Enable to let it tune the radio.";
        else if (connection != RotatorConnection.Connected) activity = "Tracking. Connect the radio to tune it.";
        else activity = $"Tuning the radio to the {(follows == RadioFollows.Uplink ? "uplink" : "downlink")}.";

        if (wanted is double w && _tracking && _armed && radio is not null && connection == RotatorConnection.Connected)
        {
            long target = (long)Math.Round(w);
            long? last;
            lock (_gate) last = _lastCommandHz;

            bool due = (last is null || Math.Abs(target - last.Value) >= step)
                       && realNow - _lastCommandReal >= TimeSpan.FromMilliseconds(200)
                       && realNow >= _backoffUntilReal;
            if (due)
            {
                await _io.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (_armed && _tracking)
                    {
                        radio.SetFrequencyHz(target);
                        lock (_gate) _lastCommandHz = target;
                        _lastCommandReal = realNow;
                        _commandFailures = 0;
                    }
                }
                catch (Exception ex)
                {
                    _lastCommandReal = realNow;
                    _backoffUntilReal = realNow + TimeSpan.FromSeconds(3); // don't hammer a radio that's saying no
                    if (++_commandFailures == 1 || _commandFailures % 20 == 0)
                        Raise($"Frequency command failed: {ex.Message}");
                }
                finally { _io.Release(); }
            }
        }

        string? description;
        long? radioHz, commanded;
        lock (_gate)
        {
            description = _radio?.Description;
            connection = _connection;
            radioHz = _radioHz;
            commanded = _lastCommandHz;
        }

        Volatile.Write(ref _snapshot, new RadioSnapshot
        {
            Connection = connection,
            Description = description,
            Armed = _armed,
            Tracking = _tracking,
            Follows = follows,
            NominalDownlinkHz = nominal,
            OffsetHz = offset,
            DownlinkHz = down,
            UplinkHz = up,
            RangeRateKmS = rr,
            RadioHz = radioHz,
            CommandedHz = commanded,
            Activity = activity,
        });
    }

    private void Raise(string message)
    {
        try { Message?.Invoke(message); } catch { /* ignore */ }
    }
}
