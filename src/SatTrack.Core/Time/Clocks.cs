namespace SatTrack.Core.Time;

public interface IClock
{
    DateTime UtcNow { get; }

    /// <summary>How many clock seconds pass per real second.</summary>
    double Rate { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTime UtcNow => DateTime.UtcNow;
    public double Rate => 1.0;
}

/// <summary>
/// A clock for simulation mode that can be moved forward and run faster than real time.
/// </summary>
public sealed class SimulationClock : IClock
{
    private readonly object _lock = new();
    private DateTime _anchorSim;
    private DateTime _anchorReal;
    private double _rate = 1.0;

    public SimulationClock() : this(DateTime.UtcNow) { }

    public SimulationClock(DateTime startUtc)
    {
        _anchorSim = startUtc;
        _anchorReal = DateTime.UtcNow;
    }

    public DateTime UtcNow
    {
        get
        {
            lock (_lock)
            {
                double elapsed = (DateTime.UtcNow - _anchorReal).TotalSeconds * _rate;
                return _anchorSim.AddSeconds(elapsed);
            }
        }
    }

    public double Rate
    {
        get { lock (_lock) return _rate; }
        set
        {
            if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value));
            lock (_lock)
            {
                Reanchor();
                _rate = value;
            }
        }
    }

    public void JumpTo(DateTime utc)
    {
        lock (_lock)
        {
            _anchorSim = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            _anchorReal = DateTime.UtcNow;
        }
    }

    public void ResetToNow()
    {
        lock (_lock)
        {
            _anchorSim = DateTime.UtcNow;
            _anchorReal = _anchorSim;
            _rate = 1.0;
        }
    }

    private void Reanchor()
    {
        var now = DateTime.UtcNow;
        _anchorSim = _anchorSim.AddSeconds((now - _anchorReal).TotalSeconds * _rate);
        _anchorReal = now;
    }
}
