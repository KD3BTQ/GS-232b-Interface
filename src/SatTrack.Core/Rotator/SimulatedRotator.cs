using SatTrack.Core.Time;

namespace SatTrack.Core.Rotator;

/// <summary>
/// A stand-in rotator for simulation mode. Moves both axes at constant speed toward the
/// commanded position, in simulation time, with the same coordinate limits as the real
/// controller (so cable-wrap and flip behaviour look the way they would on the mast).
/// Speeds default to roughly a Yaesu G-5500.
/// </summary>
public sealed class SimulatedRotator : IRotator
{
    private readonly object _lock = new();
    private readonly IClock _clock;

    private double _az, _el;
    private double? _targetAz, _targetEl;
    private DateTime _lastUpdate;
    private bool _open;

    public double AzimuthSpeedDegPerSec { get; set; } = 6.0;
    public double ElevationSpeedDegPerSec { get; set; } = 2.7;
    public double MaxAzimuth { get; set; } = 450;
    public double MaxElevation { get; set; } = 180;

    /// <summary>Report whole degrees, like the GS-232B does.</summary>
    public bool QuantizeReadings { get; set; } = true;

    public string Description => "Simulated rotator";

    public bool IsOpen { get { lock (_lock) return _open; } }

    public SimulatedRotator(IClock clock, double startAzimuth = 180, double startElevation = 0)
    {
        _clock = clock;
        _az = startAzimuth;
        _el = startElevation;
        _lastUpdate = clock.UtcNow;
    }

    public void Open()
    {
        lock (_lock)
        {
            _open = true;
            _lastUpdate = _clock.UtcNow;
        }
    }

    public void Close()
    {
        lock (_lock)
        {
            Advance();
            _targetAz = _targetEl = null;
            _open = false;
        }
    }

    public void Dispose() => Close();

    public RotatorPosition GetPosition()
    {
        lock (_lock)
        {
            EnsureOpen();
            Advance();
            return QuantizeReadings
                ? new RotatorPosition(Math.Round(_az), Math.Round(_el))
                : new RotatorPosition(_az, _el);
        }
    }

    /// <summary>Exact simulated position, for display smoothing.</summary>
    public RotatorPosition GetExactPosition()
    {
        lock (_lock)
        {
            Advance();
            return new RotatorPosition(_az, _el);
        }
    }

    public void GoTo(double azimuth, double elevation)
    {
        lock (_lock)
        {
            EnsureOpen();
            Advance();
            _targetAz = Math.Clamp(Math.Round(azimuth), 0, MaxAzimuth);
            _targetEl = Math.Clamp(Math.Round(elevation), 0, MaxElevation);
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            Advance();
            _targetAz = _targetEl = null;
        }
    }

    private void Advance()
    {
        var now = _clock.UtcNow;
        double dt = (now - _lastUpdate).TotalSeconds;
        _lastUpdate = now;

        // Ignore clock jumps (e.g. "skip to next pass"): move at most one real second's worth.
        double maxDt = Math.Max(1.0, _clock.Rate) * 1.0;
        dt = Math.Clamp(dt, 0, maxDt);
        if (dt <= 0) return;

        if (_targetAz is double ta)
        {
            double step = AzimuthSpeedDegPerSec * dt;
            double diff = ta - _az;
            if (Math.Abs(diff) <= step) { _az = ta; _targetAz = null; }
            else _az += Math.Sign(diff) * step;
        }

        if (_targetEl is double te)
        {
            double step = ElevationSpeedDegPerSec * dt;
            double diff = te - _el;
            if (Math.Abs(diff) <= step) { _el = te; _targetEl = null; }
            else _el += Math.Sign(diff) * step;
        }
    }

    private void EnsureOpen()
    {
        if (!_open) throw new InvalidOperationException("Simulated rotator is not connected.");
    }
}
