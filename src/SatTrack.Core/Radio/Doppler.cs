namespace SatTrack.Core.Radio;

/// <summary>Doppler arithmetic. Range rate is in km/s, positive when the satellite is moving away.</summary>
public static class Doppler
{
    public const double SpeedOfLightKmS = 299_792.458;

    /// <summary>Frequency heard on the ground for a satellite transmitting <paramref name="nominalHz"/>.</summary>
    public static double Downlink(double nominalHz, double rangeRateKmS) =>
        nominalHz * (1 - rangeRateKmS / SpeedOfLightKmS);

    /// <summary>Frequency to transmit so the satellite receives exactly <paramref name="nominalHz"/>.</summary>
    public static double Uplink(double nominalHz, double rangeRateKmS) =>
        nominalHz / (1 - rangeRateKmS / SpeedOfLightKmS);

    /// <summary>Downlink shift in Hz (positive when approaching).</summary>
    public static double DownlinkShift(double nominalHz, double rangeRateKmS) =>
        Downlink(nominalHz, rangeRateKmS) - nominalHz;
}
