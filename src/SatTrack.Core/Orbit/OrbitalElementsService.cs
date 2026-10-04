using System.Net.Http.Headers;
using SatTrack.Core.Catalog;
using SGPdotNET.Observation;
using SGPdotNET.Parsers;
using SGPdotNET.TLE;

namespace SatTrack.Core.Orbit;

/// <summary>A set of orbital elements for one object and where it came from.</summary>
public sealed record ElementSet(int NoradId, string Name, Tle Tle, string Source)
{
    public DateTime EpochUtc => DateTime.SpecifyKind(Tle.Epoch, DateTimeKind.Utc);
}

/// <summary>
/// Downloads and caches orbital elements.
/// Primary source is the AMSAT daily element file (one request covers every amateur satellite).
/// Anything not found there is requested from CelesTrak by catalog number, in OMM JSON format,
/// which also works for 6-digit catalog numbers that don't fit in a TLE.
/// </summary>
public sealed class OrbitalElementsService
{
    public const string AmsatUrl = "https://www.amsat.org/tle/dailytle.txt";
    public static string CelestrakUrl(int noradId) =>
        $"https://celestrak.org/NORAD/elements/gp.php?CATNR={noradId}&FORMAT=json";

    /// <summary>Refresh when the cache is older than this.</summary>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromHours(12);

    /// <summary>Upper limit on individual CelesTrak requests per refresh (be polite to CelesTrak).</summary>
    public int MaxCelestrakRequestsPerRefresh { get; set; } = 40;

    private const string AmsatCacheFile = "amsat-dailytle.txt";
    private const string CelestrakPrefix = "celestrak-";

    private readonly string _cacheDir;
    private readonly HttpClient _http;
    private readonly object _lock = new();
    private Dictionary<int, ElementSet> _sets = new();

    public DateTime? LastRefreshUtc { get; private set; }

    public bool IsStale => LastRefreshUtc is null || DateTime.UtcNow - LastRefreshUtc.Value > MaxAge;

    public int Count { get { lock (_lock) return _sets.Count; } }

    public OrbitalElementsService(string cacheDirectory, HttpClient? http = null)
    {
        _cacheDir = cacheDirectory;
        Directory.CreateDirectory(_cacheDir);

        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SatTrack", "1.0"));
    }

    /// <summary>Loads whatever is in the cache folder. Safe to call when it's empty.</summary>
    public void LoadCache()
    {
        var sets = new Dictionary<int, ElementSet>();
        DateTime? newest = null;

        string amsatPath = Path.Combine(_cacheDir, AmsatCacheFile);
        if (File.Exists(amsatPath))
        {
            foreach (var s in ParseTleText(File.ReadAllText(amsatPath), "AMSAT"))
                Merge(sets, s);
            newest = File.GetLastWriteTimeUtc(amsatPath);
        }

        foreach (string path in Directory.EnumerateFiles(_cacheDir, CelestrakPrefix + "*.json"))
        {
            try
            {
                foreach (var s in ParseOmmJson(File.ReadAllText(path), "CelesTrak"))
                    Merge(sets, s);
                var t = File.GetLastWriteTimeUtc(path);
                if (newest is null || t > newest) newest = t;
            }
            catch
            {
                // A corrupt cache file shouldn't stop the app; it'll be replaced on the next refresh.
            }
        }

        lock (_lock)
        {
            _sets = sets;
            LastRefreshUtc = newest;
        }
    }

    /// <summary>
    /// Downloads fresh elements for the given satellites. Returns a one-line summary for the status bar.
    /// Failures are reported in the summary rather than thrown, so cached data stays usable offline.
    /// </summary>
    public async Task<string> RefreshAsync(IEnumerable<SatelliteEntry> wanted, CancellationToken ct = default)
    {
        var wantedIds = wanted.Where(w => !w.HasInlineElements && w.NoradId > 0)
                              .Select(w => w.NoradId).Distinct().ToList();

        var problems = new List<string>();
        var fresh = new Dictionary<int, ElementSet>();
        bool anySuccess = false;

        // 1. AMSAT bulk file
        try
        {
            string text = await _http.GetStringAsync(AmsatUrl, ct).ConfigureAwait(false);
            var parsed = ParseTleText(text, "AMSAT").ToList();
            if (parsed.Count > 0)
            {
                await File.WriteAllTextAsync(Path.Combine(_cacheDir, AmsatCacheFile), text, ct).ConfigureAwait(false);
                foreach (var s in parsed) Merge(fresh, s);
                anySuccess = true;
            }
            else
            {
                problems.Add("AMSAT file had no usable elements");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            problems.Add($"AMSAT download failed ({ShortMessage(ex)})");
        }

        // 2. CelesTrak for anything AMSAT didn't have
        var missing = wantedIds.Where(id => !fresh.ContainsKey(id)).ToList();
        int requests = 0;
        foreach (int id in missing)
        {
            if (requests >= MaxCelestrakRequestsPerRefresh)
            {
                problems.Add($"{missing.Count - requests} satellites skipped (request limit)");
                break;
            }
            requests++;

            try
            {
                string json = await _http.GetStringAsync(CelestrakUrl(id), ct).ConfigureAwait(false);
                string trimmed = json.TrimStart();
                if (!trimmed.StartsWith('['))
                    continue; // "No GP data found" or similar: object has no public elements

                var parsed = ParseOmmJson(json, "CelesTrak").ToList();
                if (parsed.Count > 0)
                {
                    await File.WriteAllTextAsync(Path.Combine(_cacheDir, $"{CelestrakPrefix}{id}.json"), json, ct)
                              .ConfigureAwait(false);
                    foreach (var s in parsed) Merge(fresh, s);
                    anySuccess = true;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                problems.Add($"CelesTrak {id} failed ({ShortMessage(ex)})");
                if (ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden })
                    break; // being rate limited; stop asking
            }

            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        if (anySuccess)
        {
            // Reload from disk so cached CelesTrak files from earlier refreshes are included too.
            LoadCache();
            lock (_lock)
            {
                foreach (var s in fresh.Values) Merge(_sets, s);
                LastRefreshUtc = DateTime.UtcNow;
            }
        }

        int have = wantedIds.Count(id => Get(id) is not null);
        string summary = anySuccess
            ? $"Orbital data updated: {have} of {wantedIds.Count} satellites"
            : "Orbital data not updated";
        if (problems.Count > 0)
            summary += ". " + string.Join("; ", problems.Take(2)) + (problems.Count > 2 ? "…" : "");
        return summary;
    }

    public ElementSet? Get(int noradId)
    {
        lock (_lock) return _sets.TryGetValue(noradId, out var s) ? s : null;
    }

    /// <summary>
    /// Builds a propagator for a catalog entry. Inline elements in the catalog win over downloaded ones.
    /// Returns null (with a reason) when there is nothing usable.
    /// </summary>
    public Satellite? CreateSatellite(SatelliteEntry entry, out ElementSet? elements, out string? reason)
    {
        elements = null;
        reason = null;

        try
        {
            if (entry.HasInlineElements)
            {
                var tle = new Tle(entry.Name, entry.Tle1!.Trim(), entry.Tle2!.Trim());
                elements = new ElementSet((int)tle.NoradNumber, entry.Name, tle, "satellite list");
                return new Satellite(tle);
            }

            elements = Get(entry.NoradId);
            if (elements is null)
            {
                reason = $"No orbital data for {entry.Name} (NORAD {entry.NoradId}). Try Satellites ▸ Update orbital data.";
                return null;
            }

            return new Satellite(elements.Tle);
        }
        catch (Exception ex)
        {
            reason = $"Orbital data for {entry.Name} couldn't be used: {ex.Message}";
            return null;
        }
    }

    // ----------------------------------------------------------------------

    private static void Merge(Dictionary<int, ElementSet> into, ElementSet s)
    {
        if (!into.TryGetValue(s.NoradId, out var existing) || s.Tle.Epoch > existing.Tle.Epoch)
            into[s.NoradId] = s;
    }

    /// <summary>Parses 2-line or 3-line element text, skipping anything malformed.</summary>
    public static IEnumerable<ElementSet> ParseTleText(string text, string source)
    {
        var lines = text.Replace("\r", "").Split('\n')
                        .Select(l => l.TrimEnd())
                        .Where(l => l.Length > 0)
                        .ToArray();

        for (int i = 0; i < lines.Length - 1; i++)
        {
            if (!lines[i].StartsWith("1 ") || !lines[i + 1].StartsWith("2 "))
                continue;

            string name = i > 0 && !lines[i - 1].StartsWith("1 ") && !lines[i - 1].StartsWith("2 ")
                ? lines[i - 1].Trim()
                : "";

            ElementSet? set = null;
            try
            {
                var tle = new Tle(name, lines[i], lines[i + 1]);
                set = new ElementSet((int)tle.NoradNumber, tle.Name, tle, source);
            }
            catch
            {
                // Malformed element set: skip it.
            }

            if (set is not null) yield return set;
            i++;
        }
    }

    public static IEnumerable<ElementSet> ParseOmmJson(string json, string source)
    {
        var parser = new OmmJsonParser();
        foreach (var omm in parser.Parse(json))
        {
            ElementSet? set = null;
            try
            {
                var tle = new Tle(omm);
                set = new ElementSet((int)tle.NoradNumber, tle.Name, tle, source);
            }
            catch
            {
                // skip
            }
            if (set is not null) yield return set;
        }
    }

    private static string ShortMessage(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: not null } h => $"HTTP {(int)h.StatusCode.Value}",
        TaskCanceledException => "timed out",
        _ => ex.Message.Length > 60 ? ex.Message[..60] + "…" : ex.Message,
    };
}
