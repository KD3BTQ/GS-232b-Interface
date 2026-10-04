using System.Text;

namespace SatTrack.Core.Geo;

/// <summary>Maidenhead (QTH locator / grid square) conversion.</summary>
public static class Maidenhead
{
    /// <summary>
    /// Parses a 2, 4, 6 or 8 character locator (e.g. "FM19", "FM19la", "FM19la52")
    /// and returns the center of that square.
    /// </summary>
    public static bool TryParse(string? text, out double latitude, out double longitude)
    {
        latitude = longitude = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string g = text.Trim();
        if (g.Length is not (2 or 4 or 6 or 8)) return false;

        double lon = -180, lat = -90;
        double lonSize = 20, latSize = 10;

        // Field: A-R
        char f0 = char.ToUpperInvariant(g[0]), f1 = char.ToUpperInvariant(g[1]);
        if (f0 is < 'A' or > 'R' || f1 is < 'A' or > 'R') return false;
        lon += (f0 - 'A') * lonSize;
        lat += (f1 - 'A') * latSize;

        if (g.Length >= 4)
        {
            // Square: 0-9
            if (!char.IsAsciiDigit(g[2]) || !char.IsAsciiDigit(g[3])) return false;
            lonSize = 2; latSize = 1;
            lon += (g[2] - '0') * lonSize;
            lat += (g[3] - '0') * latSize;
        }

        if (g.Length >= 6)
        {
            // Subsquare: A-X
            char s0 = char.ToUpperInvariant(g[4]), s1 = char.ToUpperInvariant(g[5]);
            if (s0 is < 'A' or > 'X' || s1 is < 'A' or > 'X') return false;
            lonSize = 2.0 / 24; latSize = 1.0 / 24;
            lon += (s0 - 'A') * lonSize;
            lat += (s1 - 'A') * latSize;
        }

        if (g.Length == 8)
        {
            // Extended square: 0-9
            if (!char.IsAsciiDigit(g[6]) || !char.IsAsciiDigit(g[7])) return false;
            lonSize /= 10; latSize /= 10;
            lon += (g[6] - '0') * lonSize;
            lat += (g[7] - '0') * latSize;
        }

        longitude = lon + lonSize / 2;
        latitude = lat + latSize / 2;
        return true;
    }

    /// <summary>Converts a position to a locator of 2, 4, 6 or 8 characters.</summary>
    public static string FromLatLon(double latitude, double longitude, int length = 6)
    {
        if (length is not (2 or 4 or 6 or 8)) throw new ArgumentOutOfRangeException(nameof(length));

        double lon = Math.Clamp(longitude, -180, 179.999999) + 180;
        double lat = Math.Clamp(latitude, -90, 89.999999) + 90;
        var sb = new StringBuilder(length);

        sb.Append((char)('A' + (int)(lon / 20)));
        sb.Append((char)('A' + (int)(lat / 10)));
        lon %= 20; lat %= 10;
        if (length == 2) return sb.ToString();

        sb.Append((char)('0' + (int)(lon / 2)));
        sb.Append((char)('0' + (int)lat));
        lon %= 2; lat %= 1;
        if (length == 4) return sb.ToString();

        sb.Append((char)('a' + (int)(lon / (2.0 / 24))));
        sb.Append((char)('a' + (int)(lat / (1.0 / 24))));
        lon %= 2.0 / 24; lat %= 1.0 / 24;
        if (length == 6) return sb.ToString();

        sb.Append((char)('0' + (int)(lon / (2.0 / 240))));
        sb.Append((char)('0' + (int)(lat / (1.0 / 240))));
        return sb.ToString();
    }
}
