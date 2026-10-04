using SatTrack.Core.Geo;
using SatTrack.Core.Rotator;

namespace SatTrack.Core.Tracking;

/// <summary>What the rotator can do.</summary>
public sealed record RotatorLimits(
    double MaxAzimuth = 450,
    double MaxElevation = 90,
    double AzimuthSpeedDegPerSec = 6.0,
    double ElevationSpeedDegPerSec = 2.7)
{
    /// <summary>Elevation travel past 90° lets the antenna "flip" over the top.</summary>
    public bool CanFlip => MaxElevation >= 179;
}

/// <summary>One planned point: where the satellite is in the sky and where the rotator should point.</summary>
public readonly record struct PlanPoint(DateTime Utc, double SkyAzimuth, double SkyElevation,
                                        double RotatorAzimuth, double RotatorElevation, bool Flipped);

/// <summary>
/// The plan for one pass. It fixes, for every moment of the pass, whether the antenna is
/// flipped and which side of the azimuth overlap it uses, so the rotator never has to
/// unwind through its end stop in the middle of a pass.
/// </summary>
public sealed class PassPlan
{
    public IReadOnlyList<PlanPoint> Points { get; }
    public TimeSpan Step { get; }
    public RotatorLimits Limits { get; }

    public DateTime StartUtc => Points[0].Utc;
    public DateTime EndUtc => Points[^1].Utc;
    public bool UsesFlip { get; }
    public bool UsesOverlap { get; }

    public RotatorPosition StartPosition => new(Points[0].RotatorAzimuth, Points[0].RotatorElevation);

    internal PassPlan(IReadOnlyList<PlanPoint> points, TimeSpan step, RotatorLimits limits)
    {
        Points = points;
        Step = step;
        Limits = limits;
        UsesFlip = points.Any(p => p.Flipped);
        UsesOverlap = points.Any(p => p.RotatorAzimuth >= 360);
    }

    public bool Covers(DateTime utc) => utc >= StartUtc - Step && utc <= EndUtc + Step;

    /// <summary>
    /// Converts a live sky position into rotator coordinates, following the plan's choices.
    /// </summary>
    public RotatorPosition Map(DateTime utc, double skyAzimuth, double skyElevation)
    {
        int idx = (int)Math.Round((utc - StartUtc).TotalSeconds / Step.TotalSeconds);
        idx = Math.Clamp(idx, 0, Points.Count - 1);
        var p = Points[idx];

        double az = p.Flipped ? GeoMath.Wrap360(skyAzimuth + 180) : GeoMath.Wrap360(skyAzimuth);
        double el = p.Flipped ? 180 - skyElevation : skyElevation;
        el = Math.Clamp(el, 0, Limits.MaxElevation);

        // Choose the equivalent azimuth (az, az + 360, or the travel limit when the satellite is
        // just beyond it) closest to the planned one.
        double best = az, bestDist = double.MaxValue;
        foreach (double cand in PassPlanner.AzimuthOptions(az, Limits.MaxAzimuth))
        {
            double d = Math.Abs(cand - p.RotatorAzimuth);
            if (d < bestDist) { best = cand; bestDist = d; }
        }

        return new RotatorPosition(best, el);
    }
}

public static class PassPlanner
{
    private const double FlipPenalty = 0.02;     // per sample; prefer normal pointing when it costs nothing
    private const double OverlapPenalty = 0.005; // per sample; prefer the primary 0-360 range
    // Costs are in "degree-seconds of pointing error". A second of lag while slewing a long way
    // is roughly a 90° pointing error; waiting at a travel limit costs its actual error.
    private const double LagWeight = 90.0;
    private const double ErrorWeight = 1.0;
    private const double MaxClampDeg = 60;       // how far past a travel limit we'll wait instead of unwinding

    private readonly record struct State(double Az, double El, bool Flipped, double ErrorDeg);

    /// <summary>
    /// Builds a plan from sky positions sampled at a fixed step.
    /// Uses dynamic programming over the possible rotator positions for each sample (normal or
    /// flipped; az or az + 360 in the overlap; or waiting at a travel limit) to minimise motion,
    /// lag and pointing error. Lag and error count for more at high elevation, so any unavoidable
    /// unwind happens near the horizon rather than in the middle of the pass.
    /// </summary>
    public static PassPlan Build(
        IReadOnlyList<(DateTime Utc, double Azimuth, double Elevation)> samples,
        TimeSpan step,
        RotatorLimits limits,
        RotatorPosition? startFrom = null)
    {
        if (samples.Count == 0) throw new ArgumentException("No samples.", nameof(samples));

        int n = samples.Count;
        var states = new List<State>[n];
        var weight = new double[n];
        for (int i = 0; i < n; i++)
        {
            states[i] = Candidates(samples[i].Azimuth, samples[i].Elevation, limits);
            weight[i] = 1 + Math.Max(0, samples[i].Elevation) / 15.0;
        }

        var cost = new double[n][];
        var back = new int[n][];
        double dt = step.TotalSeconds;

        cost[0] = new double[states[0].Count];
        back[0] = new int[states[0].Count];
        for (int s = 0; s < states[0].Count; s++)
        {
            var st = states[0][s];
            double c = startFrom is RotatorPosition sp
                ? MoveTime(sp.Azimuth, sp.Elevation, st.Az, st.El, limits)
                : 0;
            cost[0][s] = c + Penalty(st, weight[0], dt);
            back[0][s] = -1;
        }

        for (int i = 1; i < n; i++)
        {
            cost[i] = new double[states[i].Count];
            back[i] = new int[states[i].Count];
            for (int s = 0; s < states[i].Count; s++)
            {
                var st = states[i][s];
                double best = double.MaxValue;
                int bestPrev = 0;
                for (int p = 0; p < states[i - 1].Count; p++)
                {
                    var prev = states[i - 1][p];
                    double move = MoveTime(prev.Az, prev.El, st.Az, st.El, limits);
                    double c = cost[i - 1][p] + move + LagWeight * weight[i] * Math.Max(0, move - dt);
                    if (c < best) { best = c; bestPrev = p; }
                }
                cost[i][s] = best + Penalty(st, weight[i], dt);
                back[i][s] = bestPrev;
            }
        }

        // Trace back the cheapest path.
        int idx = 0;
        for (int s = 1; s < states[n - 1].Count; s++)
            if (cost[n - 1][s] < cost[n - 1][idx]) idx = s;

        var points = new PlanPoint[n];
        for (int i = n - 1; i >= 0; i--)
        {
            var st = states[i][idx];
            points[i] = new PlanPoint(samples[i].Utc, samples[i].Azimuth, samples[i].Elevation, st.Az, st.El, st.Flipped);
            idx = back[i][idx];
        }

        return new PassPlan(points, step, limits);
    }

    /// <summary>
    /// Rotator azimuths that can represent a bearing: every equivalent within [0, maxAz],
    /// plus the nearest travel limit when the bearing is only just out of reach.
    /// </summary>
    internal static IEnumerable<double> AzimuthOptions(double az, double maxAz)
    {
        az = GeoMath.Wrap360(az);
        bool any = false;
        for (double cand = az; cand <= maxAz + 1e-9; cand += 360)
        {
            any = true;
            yield return cand;
        }

        // Just past the upper limit (e.g. 455° on a 450° rotator): wait at the limit.
        double over = az + 360 * Math.Ceiling((maxAz - az) / 360.0 + 1e-9);
        if (over > maxAz && over - maxAz <= MaxClampDeg) yield return maxAz;

        // Just below zero (e.g. 355° seen as -5°): wait at 0.
        if (az - 360 < 0 && az - 360 >= -MaxClampDeg) yield return 0;

        if (!any) yield return Math.Min(az, maxAz);
    }

    private static List<State> Candidates(double skyAz, double skyEl, RotatorLimits limits)
    {
        var list = new List<State>(8);
        double el = Math.Clamp(skyEl, 0, Math.Min(90, limits.MaxElevation));
        AddAzimuths(list, GeoMath.Wrap360(skyAz), el, false, limits);

        if (limits.CanFlip)
        {
            double fel = Math.Clamp(180 - skyEl, 90, limits.MaxElevation);
            AddAzimuths(list, GeoMath.Wrap360(skyAz + 180), fel, true, limits);
        }
        return list;
    }

    private static void AddAzimuths(List<State> list, double az, double el, bool flipped, RotatorLimits limits)
    {
        foreach (double cand in AzimuthOptions(az, limits.MaxAzimuth))
        {
            double err = Math.Abs(GeoMath.Wrap180(cand - az));
            if (list.Any(s => s.Flipped == flipped && Math.Abs(s.Az - cand) < 1e-6)) continue;
            list.Add(new State(cand, el, flipped, err));
        }
    }

    private static double Penalty(State s, double weight, double dt) =>
        (s.Flipped ? FlipPenalty : 0) + (s.Az >= 360 ? OverlapPenalty : 0) + ErrorWeight * weight * s.ErrorDeg * dt;

    private static double MoveTime(double az1, double el1, double az2, double el2, RotatorLimits limits) =>
        Math.Max(Math.Abs(az2 - az1) / limits.AzimuthSpeedDegPerSec,
                 Math.Abs(el2 - el1) / limits.ElevationSpeedDegPerSec);
}
