namespace SatTrack.Core.Radio;

/// <summary>Radio control protocol.</summary>
public enum RadioType { FlexCat, IcomCiv }

/// <summary>A radio whose frequency can be read and set. Calls may block briefly on I/O.</summary>
public interface IRadio : IDisposable
{
    /// <summary>Human-readable description, e.g. "ICOM CI-V (74h) on COM4".</summary>
    string Description { get; }

    bool IsOpen { get; }

    void Open();
    void Close();

    /// <summary>Frequency of the operating VFO, in Hz.</summary>
    long GetFrequencyHz();

    /// <summary>Sets the operating VFO frequency, in Hz.</summary>
    void SetFrequencyHz(long hz);
}
