using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SatTrack.Core.Catalog;

/// <summary>One satellite the user can pick from the list.</summary>
public sealed class SatelliteEntry
{
    /// <summary>Display name.</summary>
    public string Name { get; set; } = "";

    /// <summary>NORAD catalog number. Used to look up orbital elements online.</summary>
    public int NoradId { get; set; }

    /// <summary>Optional free text shown as a tooltip (mode, frequencies, schedule...).</summary>
    public string? Notes { get; set; }

    /// <summary>
    /// Optional inline two-line elements. When present these are always used instead of
    /// downloaded data, which is handy for new launches or objects without public elements.
    /// </summary>
    public string? Tle1 { get; set; }
    public string? Tle2 { get; set; }

    [JsonIgnore]
    public bool HasInlineElements => !string.IsNullOrWhiteSpace(Tle1) && !string.IsNullOrWhiteSpace(Tle2);

    public override string ToString() => Name;
}

/// <summary>The on-disk satellite list format.</summary>
public sealed class SatelliteCatalogFile
{
    public string? Description { get; set; }
    public List<SatelliteEntry> Satellites { get; set; } = new();
}

public sealed class SatelliteCatalog
{
    public const string ResourceName = "SatTrack.Core.DefaultSatellites.json";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public IReadOnlyList<SatelliteEntry> Satellites { get; }

    /// <summary>Where the list came from: "Built-in list" or a file path.</summary>
    public string Source { get; }

    public bool IsBuiltIn { get; }

    /// <summary>Problems found while loading (skipped entries). Empty if everything loaded.</summary>
    public IReadOnlyList<string> Warnings { get; }

    private SatelliteCatalog(IReadOnlyList<SatelliteEntry> sats, string source, bool builtIn, IReadOnlyList<string> warnings)
    {
        Satellites = sats;
        Source = source;
        IsBuiltIn = builtIn;
        Warnings = warnings;
    }

    /// <summary>The raw JSON of the built-in list (used for "Export built-in list").</summary>
    public static string GetBuiltInJson()
    {
        var asm = typeof(SatelliteCatalog).Assembly;
        using var stream = asm.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static SatelliteCatalog LoadBuiltIn() => Parse(GetBuiltInJson(), "Built-in list", builtIn: true);

    public static SatelliteCatalog LoadFromFile(string path)
    {
        string json = File.ReadAllText(path);
        return Parse(json, path, builtIn: false);
    }

    public static SatelliteCatalog Parse(string json, string source, bool builtIn = false)
    {
        SatelliteCatalogFile? file;
        try
        {
            file = JsonSerializer.Deserialize<SatelliteCatalogFile>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"The satellite list isn't valid JSON: {ex.Message}", ex);
        }

        if (file?.Satellites is null || file.Satellites.Count == 0)
            throw new FormatException("The satellite list has no entries. It needs a \"satellites\" array.");

        var warnings = new List<string>();
        var result = new List<SatelliteEntry>();
        var seen = new HashSet<int>();

        for (int i = 0; i < file.Satellites.Count; i++)
        {
            var s = file.Satellites[i];
            if (s is null) continue;

            s.Name = s.Name?.Trim() ?? "";
            if (s.Name.Length == 0)
                s.Name = s.NoradId > 0 ? $"NORAD {s.NoradId}" : "";

            if (s.NoradId <= 0 && !s.HasInlineElements)
            {
                warnings.Add($"Entry {i + 1} ('{s.Name}') skipped: it needs a noradId or tle1/tle2 lines.");
                continue;
            }

            if (s.NoradId > 0 && !seen.Add(s.NoradId))
            {
                warnings.Add($"Entry {i + 1} ('{s.Name}') skipped: NORAD {s.NoradId} is already in the list.");
                continue;
            }

            result.Add(s);
        }

        if (result.Count == 0)
            throw new FormatException("None of the entries in the satellite list could be used.");

        return new SatelliteCatalog(result, source, builtIn, warnings);
    }
}
