namespace SatTrack.Core.Rotator;

public readonly record struct RotatorPosition(double Azimuth, double Elevation);

/// <summary>
/// A rotator controller. Methods are synchronous and may block briefly on I/O;
/// the tracking engine calls them from a background thread.
/// </summary>
public interface IRotator : IDisposable
{
    /// <summary>Human-readable description, e.g. "GS-232B on COM3".</summary>
    string Description { get; }

    bool IsOpen { get; }

    void Open();
    void Close();

    RotatorPosition GetPosition();

    /// <summary>Move to the given position, in rotator coordinates (azimuth may exceed 360 in 450° mode).</summary>
    void GoTo(double azimuth, double elevation);

    /// <summary>Stop all motion immediately.</summary>
    void Stop();
}
