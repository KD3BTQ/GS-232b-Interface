namespace SatTrack.Core.Geo;

public readonly record struct GeoPoint(double Latitude, double Longitude);

public static class GeoMath
{
    public const double EarthRadiusKm = 6378.137;
    public const double Deg = Math.PI / 180.0;

    /// <summary>Wraps an angle into [0, 360).</summary>
    public static double Wrap360(double deg)
    {
        double r = deg % 360.0;
        return r < 0 ? r + 360.0 : r;
    }

    /// <summary>Wraps an angle into [-180, 180).</summary>
    public static double Wrap180(double deg) => Wrap360(deg + 180.0) - 180.0;

    /// <summary>Smallest absolute difference between two bearings, in degrees.</summary>
    public static double AngularDistance(double a, double b) => Math.Abs(Wrap180(a - b));

    /// <summary>Great-circle angular distance and initial bearing (both degrees) from p1 to p2.</summary>
    public static (double DistanceDeg, double BearingDeg) DistanceBearing(GeoPoint p1, GeoPoint p2)
    {
        double lat1 = p1.Latitude * Deg, lat2 = p2.Latitude * Deg;
        double dLon = (p2.Longitude - p1.Longitude) * Deg;

        double sinDLat = Math.Sin((lat2 - lat1) / 2), sinDLon = Math.Sin(dLon / 2);
        double a = sinDLat * sinDLat + Math.Cos(lat1) * Math.Cos(lat2) * sinDLon * sinDLon;
        double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(Math.Max(0, 1 - a)));

        double y = Math.Sin(dLon) * Math.Cos(lat2);
        double x = Math.Cos(lat1) * Math.Sin(lat2) - Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(dLon);

        return (c / Deg, Wrap360(Math.Atan2(y, x) / Deg));
    }

    /// <summary>Destination point given a start, a bearing and an angular distance (degrees).</summary>
    public static GeoPoint Destination(GeoPoint start, double bearingDeg, double distanceDeg)
    {
        double lat1 = start.Latitude * Deg, lon1 = start.Longitude * Deg;
        double brg = bearingDeg * Deg, d = distanceDeg * Deg;

        double lat2 = Math.Asin(Math.Clamp(
            Math.Sin(lat1) * Math.Cos(d) + Math.Cos(lat1) * Math.Sin(d) * Math.Cos(brg), -1, 1));
        double lon2 = lon1 + Math.Atan2(Math.Sin(brg) * Math.Sin(d) * Math.Cos(lat1),
                                        Math.Cos(d) - Math.Sin(lat1) * Math.Sin(lat2));
        return new GeoPoint(lat2 / Deg, Wrap180(lon2 / Deg));
    }

    /// <summary>
    /// Earth-central angle (degrees) of the area that can see a satellite at 0° elevation.
    /// </summary>
    public static double FootprintRadiusDeg(double altitudeKm)
    {
        if (altitudeKm <= 0) return 0;
        return Math.Acos(EarthRadiusKm / (EarthRadiusKm + altitudeKm)) / Deg;
    }

    /// <summary>Points on a small circle of the given angular radius around a center.</summary>
    public static GeoPoint[] SmallCircle(GeoPoint center, double radiusDeg, int points = 120)
    {
        var result = new GeoPoint[points];
        for (int i = 0; i < points; i++)
            result[i] = Destination(center, 360.0 * i / points, radiusDeg);
        return result;
    }
}
