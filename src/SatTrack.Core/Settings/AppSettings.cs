using System.Text.Json;
using SatTrack.Core.Geo;
using SatTrack.Core.Tracking;

namespace SatTrack.Core.Settings;

public enum MapProjection { Mercator, Planar }

public sealed class AppSettings
{
    // Station
    public string LocationText { get; set; } = "";
    public double AltitudeMeters { get; set; }

    // Rotator
    public string ComPort { get; set; } = "COM3";
    public int BaudRate { get; set; } = 9600;
    public int MaxAzimuth { get; set; } = 450;
    public int MaxElevation { get; set; } = 90;

    /// <summary>Send a new position when the target has moved at least this far (degrees).</summary>
    public double CommandThresholdDeg { get; set; } = 2.0;

    /// <summary>Aim this far ahead of the satellite to make up for rotator lag.</summary>
    public double LeadSeconds { get; set; } = 2.0;

    /// <summary>Move to the rise position this many minutes before the pass starts.</summary>
    public double PrePositionMinutes { get; set; } = 2.0;

    public bool ParkAfterPass { get; set; }
    public double ParkAzimuth { get; set; } = 180;
    public double ParkElevation { get; set; }

    // Satellites
    /// <summary>Path of a user satellite list, or null for the built-in list.</summary>
    public string? CatalogPath { get; set; }
    public int? SelectedNoradId { get; set; }
    public string? SelectedName { get; set; }

    // View
    public MapProjection Projection { get; set; } = MapProjection.Mercator;
    public bool DarkMode { get; set; } = true;
    public bool AlwaysOnTop { get; set; }
    public int[]? WindowBounds { get; set; }
    public double SimulationSpeed { get; set; } = 1.0;

    public StationLocation? GetStation()
    {
        return StationLocation.TryParse(LocationText, AltitudeMeters, out var loc, out _) ? loc : null;
    }

    public RotatorLimits GetLimits() => new(MaxAzimuth, MaxElevation);

    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this))!;
}

public static class SettingsStore
{
    public static string AppDataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SatTrack");

    public static string SettingsPath => Path.Combine(AppDataFolder, "settings.json");

    public static string ElementCacheFolder => Path.Combine(AppDataFolder, "elements");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Options) ?? new AppSettings();
        }
        catch
        {
            // Unreadable settings: start fresh rather than refusing to open.
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(AppDataFolder);
        string tmp = SettingsPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
        File.Move(tmp, SettingsPath, overwrite: true);
    }
}
