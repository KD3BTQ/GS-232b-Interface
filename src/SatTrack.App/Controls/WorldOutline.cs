using System.Globalization;

namespace SatTrack.App.Controls;

/// <summary>One land polygon ring or boundary line, as lon/lat pairs.</summary>
public sealed class OutlineShape
{
    public required bool IsLand { get; init; }
    public required float[] Lon { get; init; }
    public required float[] Lat { get; init; }
    public float MinLon { get; init; }
    public float MaxLon { get; init; }
}

/// <summary>
/// Coastlines and country borders from Natural Earth (public domain), embedded in the exe
/// so the map works offline.
/// </summary>
public static class WorldOutline
{
    private static IReadOnlyList<OutlineShape>? _shapes;

    public static IReadOnlyList<OutlineShape> Shapes => _shapes ??= Load();

    private static IReadOnlyList<OutlineShape> Load()
    {
        var list = new List<OutlineShape>();
        using var stream = typeof(WorldOutline).Assembly.GetManifestResourceStream("SatTrack.App.world.txt");
        if (stream is null) return list;

        using var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length < 3 || line[0] == '#') continue;
            bool land = line[0] == 'P';
            var parts = line.Substring(2).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var lon = new float[parts.Length];
            var lat = new float[parts.Length];
            int n = 0;
            foreach (var p in parts)
            {
                int comma = p.IndexOf(',');
                if (comma < 0) continue;
                lon[n] = float.Parse(p.AsSpan(0, comma), NumberStyles.Float, CultureInfo.InvariantCulture);
                lat[n] = float.Parse(p.AsSpan(comma + 1), NumberStyles.Float, CultureInfo.InvariantCulture);
                n++;
            }
            if (n < 2) continue;
            Array.Resize(ref lon, n);
            Array.Resize(ref lat, n);
            list.Add(new OutlineShape { IsLand = land, Lon = lon, Lat = lat, MinLon = lon.Min(), MaxLon = lon.Max() });
        }
        return list;
    }
}
