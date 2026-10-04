using SatTrack.Core.Geo;
using SGPdotNET.CoordinateSystem;
using SGPdotNET.Observation;
using SGPdotNET.Util;

namespace SatTrack.Core.Orbit;

/// <summary>Where the satellite is in the sky, seen from the station.</summary>
public readonly record struct LookAngles(double Azimuth, double Elevation, double RangeKm, double RangeRateKmS);

/// <summary>The point on Earth directly below the satellite.</summary>
public readonly record struct SubPoint(double Latitude, double Longitude, double AltitudeKm)
{
    public GeoPoint Point => new(Latitude, Longitude);
}

/// <summary>A period during which the satellite is above the horizon.</summary>
public sealed record PassInfo(
    DateTime AosUtc,
    DateTime LosUtc,
    DateTime MaxElevationUtc,
    double MaxElevation,
    double AosAzimuth,
    double LosAzimuth,
    bool NeverSets)
{
    public bool IsInProgress(DateTime utc) => utc >= AosUtc && utc <= LosUtc;
    public TimeSpan Duration => LosUtc - AosUtc;
}

/// <summary>
/// Wraps an SGP4 propagator and a ground station. Methods never throw for propagation problems
/// (e.g. decayed objects); they return null instead.
/// </summary>
public sealed class SatellitePredictor
{
    private readonly Satellite _satellite;
    private readonly GroundStation _station;

    public string Name { get; }
    public ElementSet? Elements { get; }
    public StationLocation Station { get; }

    /// <summary>Orbital period from the mean motion.</summary>
    public TimeSpan Period { get; }

    public SatellitePredictor(Satellite satellite, StationLocation station, string name, ElementSet? elements)
    {
        _satellite = satellite;
        Station = station;
        Name = name;
        Elements = elements;
        _station = new GroundStation(new GeodeticCoordinate(
            Angle.FromDegrees(station.Latitude),
            Angle.FromDegrees(station.Longitude),
            station.AltitudeMeters / 1000.0));

        double revPerDay = satellite.Tle.MeanMotionRevPerDay;
        Period = revPerDay > 0 ? TimeSpan.FromMinutes(1440.0 / revPerDay) : TimeSpan.FromMinutes(100);
    }

    public LookAngles? Observe(DateTime utc)
    {
        try
        {
            var o = _station.Observe(_satellite, Utc(utc));
            return new LookAngles(GeoMath.Wrap360(o.Azimuth.Degrees), o.Elevation.Degrees, o.Range, o.RangeRate);
        }
        catch
        {
            return null;
        }
    }

    public SubPoint? GetSubPoint(DateTime utc)
    {
        try
        {
            var g = _satellite.Predict(Utc(utc)).ToGeodetic();
            return new SubPoint(g.Latitude.Degrees, GeoMath.Wrap180(g.Longitude.Degrees), g.Altitude);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Sub-satellite points between two times.</summary>
    public List<GeoPoint> GroundTrack(DateTime fromUtc, DateTime toUtc, TimeSpan step)
    {
        var list = new List<GeoPoint>();
        for (var t = fromUtc; t <= toUtc; t += step)
        {
            var p = GetSubPoint(t);
            if (p is not null) list.Add(p.Value.Point);
        }
        return list;
    }

    private double El(DateTime t) => Observe(t)?.Elevation ?? -90;

    /// <summary>
    /// Returns the pass in progress at <paramref name="fromUtc"/>, or the next one within the search window.
    /// Objects that stay above the horizon for the whole window (geostationary) are reported with NeverSets.
    /// </summary>
    public PassInfo? FindPass(DateTime fromUtc, TimeSpan searchWindow, double horizonDeg = 0)
    {
        fromUtc = Utc(fromUtc);
        var coarse = CoarseStep();
        var end = fromUtc + searchWindow;

        DateTime aos;
        if (El(fromUtc) > horizonDeg)
        {
            // Pass in progress: look back for the rise time.
            var t = fromUtc;
            var lookBackLimit = fromUtc - TimeSpan.FromHours(Math.Max(2, Period.TotalHours));
            while (t > lookBackLimit && El(t - coarse) > horizonDeg) t -= coarse;
            aos = t <= lookBackLimit ? lookBackLimit : Refine(t - coarse, t, horizonDeg, rising: true);
        }
        else
        {
            var t = fromUtc;
            while (t < end && El(t) <= horizonDeg) t += coarse;
            if (t >= end) return null;
            aos = Refine(t - coarse, t, horizonDeg, rising: true);
        }

        // Find set time.
        DateTime los;
        bool neverSets = false;
        {
            var t = aos + TimeSpan.FromSeconds(1);
            var losLimit = Max(end, aos + TimeSpan.FromHours(2));
            while (t < losLimit && El(t) > horizonDeg) t += coarse;
            if (t >= losLimit)
            {
                neverSets = true;
                los = losLimit;
            }
            else
            {
                los = Refine(t - coarse, t, horizonDeg, rising: false);
            }
        }

        // Max elevation.
        double maxEl = double.MinValue;
        DateTime maxT = aos;
        var span = los - aos;
        var sampleStep = TimeSpan.FromSeconds(Math.Max(5, span.TotalSeconds / 400));
        for (var t = aos; t <= los; t += sampleStep)
        {
            double e = El(t);
            if (e > maxEl) { maxEl = e; maxT = t; }
        }

        double aosAz = Observe(aos)?.Azimuth ?? 0;
        double losAz = Observe(los)?.Azimuth ?? 0;
        return new PassInfo(aos, los, maxT, maxEl, aosAz, losAz, neverSets);
    }

    /// <summary>Bisection for the horizon crossing between two times.</summary>
    private DateTime Refine(DateTime before, DateTime after, double horizon, bool rising)
    {
        for (int i = 0; i < 20 && (after - before).TotalSeconds > 0.5; i++)
        {
            var mid = before + TimeSpan.FromTicks((after - before).Ticks / 2);
            bool above = El(mid) > horizon;
            if (above == rising) after = mid; else before = mid;
        }
        return rising ? after : before;
    }

    private TimeSpan CoarseStep()
    {
        // LEO passes last minutes; higher orbits can use coarser steps.
        double minutes = Period.TotalMinutes;
        return minutes < 200 ? TimeSpan.FromSeconds(20)
             : minutes < 800 ? TimeSpan.FromSeconds(60)
             : TimeSpan.FromMinutes(5);
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    private static DateTime Utc(DateTime t) =>
        t.Kind == DateTimeKind.Utc ? t : t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : DateTime.SpecifyKind(t, DateTimeKind.Utc);
}
