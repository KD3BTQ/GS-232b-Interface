using System.Globalization;
using System.Text.RegularExpressions;

namespace SatTrack.Core.Geo;

/// <summary>The rotator's position on Earth.</summary>
public sealed record StationLocation(double Latitude, double Longitude, double AltitudeMeters = 0)
{
    public string GridSquare => Maidenhead.FromLatLon(Latitude, Longitude, 6);

    public GeoPoint Point => new(Latitude, Longitude);

    public override string ToString() =>
        string.Format(CultureInfo.InvariantCulture, "{0:0.0000}°, {1:0.0000}° ({2})", Latitude, Longitude, GridSquare);

    private static readonly Regex DecimalPair = new(
        @"^\s*(?<lat>[-+]?\d+(?:\.\d+)?)\s*°?\s*(?<ns>[NnSs])?\s*[,;\s]\s*(?<lon>[-+]?\d+(?:\.\d+)?)\s*°?\s*(?<ew>[EeWw])?\s*$",
        RegexOptions.Compiled);

    /// <summary>
    /// Accepts a grid square ("FM19la") or decimal degrees ("39.29, -76.61" or "39.29N 76.61W").
    /// </summary>
    public static bool TryParse(string? text, double altitudeMeters, out StationLocation? location, out string? error)
    {
        location = null;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Enter a grid square (like FM19la) or latitude, longitude (like 39.29, -76.61).";
            return false;
        }

        string t = text.Trim();

        if (Maidenhead.TryParse(t, out double gLat, out double gLon))
        {
            location = new StationLocation(gLat, gLon, altitudeMeters);
            return true;
        }

        var m = DecimalPair.Match(t);
        if (m.Success)
        {
            double lat = double.Parse(m.Groups["lat"].Value, CultureInfo.InvariantCulture);
            double lon = double.Parse(m.Groups["lon"].Value, CultureInfo.InvariantCulture);
            if (m.Groups["ns"].Success && char.ToUpperInvariant(m.Groups["ns"].Value[0]) == 'S') lat = -Math.Abs(lat);
            if (m.Groups["ew"].Success && char.ToUpperInvariant(m.Groups["ew"].Value[0]) == 'W') lon = -Math.Abs(lon);

            if (lat is < -90 or > 90) { error = "Latitude must be between -90 and 90."; return false; }
            if (lon is < -180 or > 180) { error = "Longitude must be between -180 and 180."; return false; }

            location = new StationLocation(lat, lon, altitudeMeters);
            return true;
        }

        error = "That isn't a grid square or a latitude, longitude pair. Examples: FM19la or 39.29, -76.61";
        return false;
    }
}
