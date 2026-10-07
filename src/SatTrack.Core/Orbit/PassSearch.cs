using System.Collections.Concurrent;
using SatTrack.Core.Catalog;
using SatTrack.Core.Geo;

namespace SatTrack.Core.Orbit;

/// <summary>One row of the Upcoming Passes table.</summary>
public sealed record PassListItem(SatelliteEntry Satellite, PassInfo Pass, IReadOnlyList<GeoPoint> Track);

public sealed record PassSearchResult(
    IReadOnlyList<PassListItem> Passes,
    int SatellitesSearched,
    IReadOnlyList<string> SkippedSatellites);

/// <summary>Finds every pass of every satellite in a list within a time window.</summary>
public static class PassSearch
{
    /// <param name="minMaxElevation">Leave out passes whose highest point is below this.</param>
    /// <param name="progress">Reports satellites finished so far.</param>
    public static PassSearchResult Run(
        IReadOnlyList<SatelliteEntry> satellites,
        OrbitalElementsService elements,
        StationLocation station,
        DateTime fromUtc,
        DateTime toUtc,
        double minMaxElevation,
        IProgress<int>? progress,
        CancellationToken ct)
    {
        var found = new ConcurrentBag<PassListItem>();
        var skipped = new ConcurrentBag<string>();
        int done = 0;

        var options = new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1),
        };

        Parallel.ForEach(satellites, options, entry =>
        {
            try
            {
                var sat = elements.CreateSatellite(entry, out var set, out _);
                if (sat is null)
                {
                    skipped.Add(entry.Name);
                    return;
                }

                var predictor = new SatellitePredictor(sat, station, entry.Name, set);
                var t = fromUtc;
                int guard = 0;
                while (t < toUtc && guard++ < 2000)
                {
                    ct.ThrowIfCancellationRequested();
                    var pass = predictor.FindPass(t, toUtc - t);
                    if (pass is null) break;

                    if (pass.MaxElevation >= minMaxElevation)
                    {
                        var track = pass.NeverSets
                            ? (IReadOnlyList<GeoPoint>)Array.Empty<GeoPoint>()
                            : predictor.GroundTrack(pass.AosUtc, pass.LosUtc,
                                TimeSpan.FromSeconds(Math.Clamp(pass.Duration.TotalSeconds / 60, 10, 300)));
                        found.Add(new PassListItem(entry, pass, track));
                    }

                    if (pass.NeverSets) break;
                    t = pass.LosUtc + TimeSpan.FromMinutes(1);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                skipped.Add(entry.Name);
            }
            finally
            {
                progress?.Report(Interlocked.Increment(ref done));
            }
        });

        var list = found.OrderBy(p => p.Pass.AosUtc).ThenBy(p => p.Satellite.Name).ToList();
        return new PassSearchResult(list, satellites.Count, skipped.OrderBy(s => s).ToList());
    }
}
