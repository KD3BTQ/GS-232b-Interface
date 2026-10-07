using System.Drawing.Drawing2D;
using SatTrack.Core.Geo;
using SatTrack.Core.Settings;
using SatTrack.Core.Tracking;
using SGPdotNET.Propagation.Bodies;

namespace SatTrack.App.Controls;

/// <summary>A ground track drawn on the map. Highlighted traces are bold and labelled.</summary>
public sealed record MapTrace(IReadOnlyList<GeoPoint> Points, bool Highlight, string? Label);

/// <summary>
/// World map with the station, satellite, ground track, coverage circle and day/night shading.
/// The coastlines are rendered into a cached bitmap; only the moving parts redraw each frame.
/// </summary>
public sealed class MapView : Control
{
    private Palette _palette = Palette.Dark;
    private MapProjection _projection = MapProjection.Mercator;
    private TrackerSnapshot? _snapshot;
    private double _zoom = 1;

    private Bitmap? _base;
    private string _baseKey = "";

    private GeoPoint? _subSolar;
    private DateTime _subSolarTime;

    private Rectangle[] _projChips = Array.Empty<Rectangle>();
    private Rectangle[] _themeChips = Array.Empty<Rectangle>();

    /// <summary>Extra ground tracks to draw (used by the Upcoming Passes window).</summary>
    public IReadOnlyList<MapTrace> Traces { get; set; } = Array.Empty<MapTrace>();

    /// <summary>Show the satellite info box in the top-left corner.</summary>
    public bool ShowInfoBox { get; set; } = true;

    /// <summary>Show the Dark/Light chips (the main window owns the theme).</summary>
    public bool ShowThemeChips { get; set; } = true;

    public event Action<MapProjection>? ProjectionChosen;
    public event Action<bool>? DarkModeChosen;

    public MapView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        TabStop = false;
    }

    public Palette Palette
    {
        get => _palette;
        set { _palette = value; Invalidate(); }
    }

    public MapProjection Projection
    {
        get => _projection;
        set { if (_projection != value) { _projection = value; _zoom = DefaultZoom; Invalidate(); } }
    }

    /// <summary>Planar opens zoomed to about 6,500 km around the station, where passes happen.</summary>
    private double DefaultZoom => _projection == MapProjection.Planar ? 3 : 1;

    public TrackerSnapshot? Snapshot
    {
        get => _snapshot;
        set { _snapshot = value; Invalidate(); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _base?.Dispose();
        base.Dispose(disposing);
    }

    // ------------------------------------------------------------------ input

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        double factor = Math.Pow(1.25, e.Delta / 120.0);
        _zoom = Math.Clamp(_zoom * factor, 1, 12);
        Invalidate();
        base.OnMouseWheel(e);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        if (!HitChip(e.Location)) { _zoom = DefaultZoom; Invalidate(); }
        base.OnMouseDoubleClick(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (_projChips.Length == 2)
        {
            if (_projChips[0].Contains(e.Location)) ProjectionChosen?.Invoke(MapProjection.Mercator);
            else if (_projChips[1].Contains(e.Location)) ProjectionChosen?.Invoke(MapProjection.Planar);
        }
        if (_themeChips.Length == 2)
        {
            if (_themeChips[0].Contains(e.Location)) DarkModeChosen?.Invoke(true);
            else if (_themeChips[1].Contains(e.Location)) DarkModeChosen?.Invoke(false);
        }
        base.OnMouseClick(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        Cursor = HitChip(e.Location) ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }

    private bool HitChip(Point p) => _projChips.Concat(_themeChips).Any(r => r.Contains(p));

    // ------------------------------------------------------------------ painting

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        float k = g.DpiX / 96f;
        int w = ClientSize.Width, h = ClientSize.Height;
        if (w < 20 || h < 20) return;

        g.Clear(_palette.Window);

        var snap = _snapshot;
        var station = snap?.Station;
        var center = station?.Point ?? new GeoPoint(20, 0);

        MapProjector proj = _projection == MapProjection.Mercator
            ? new MercatorProjector(w, h, center.Latitude, center.Longitude, _zoom)
            : new PlanarProjector(w, h, center, _zoom);

        // Cached coastlines.
        string key = $"{w}x{h}|{_projection}|{_zoom:0.###}|{center.Latitude:0.###},{center.Longitude:0.###}|{_palette.Name}|{k}";
        if (_base is null || key != _baseKey)
        {
            _base?.Dispose();
            _base = RenderBase(proj, w, h, k);
            _baseKey = key;
        }
        g.DrawImageUnscaled(_base, 0, 0);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        var savedClip = g.Save();
        proj.SetClip(g);

        DrawNight(g, proj, snap?.UtcNow ?? DateTime.UtcNow);
        DrawTraces(g, proj, k);

        if (snap is not null)
            DrawSatellite(g, proj, snap, k);

        if (station is not null && proj.TryPoint(station.Latitude, station.Longitude, out var sp))
            DrawStation(g, sp, station.GridSquare, k);

        g.Restore(savedClip);

        if (ShowInfoBox) DrawInfo(g, snap, w, h, k);
        DrawChips(g, w, h, k);
    }

    private Bitmap RenderBase(MapProjector proj, int w, int h, float k)
    {
        var bmp = new Bitmap(w, h);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(_palette.Window);

        proj.SetClip(g);
        using (var ocean = new SolidBrush(_palette.Ocean)) g.FillRectangle(ocean, 0, 0, w, h);

        using (var grid = new Pen(_palette.Graticule, 1f * k))
            proj.DrawGrid(g, grid, _palette, 10f * k);

        using var land = new GraphicsPath(FillMode.Winding);
        using var borders = new GraphicsPath();
        foreach (var shape in WorldOutline.Shapes)
            proj.AddShape(shape.IsLand ? land : borders, shape, shape.IsLand);

        using (var landBrush = new SolidBrush(_palette.Land)) g.FillPath(landBrush, land);
        using (var borderPen = new Pen(_palette.Border, 0.8f * k)) g.DrawPath(borderPen, borders);
        using (var coastPen = new Pen(_palette.Coast, 1f * k)) g.DrawPath(coastPen, land);

        // Grid labels drawn on top of land in planar mode need the grid again, faintly.
        if (proj is PlanarProjector)
        {
            using var grid2 = new Pen(Color.FromArgb(70, _palette.Graticule), 1f * k);
            proj.DrawGrid(g, grid2, _palette, 10f * k);
        }

        return bmp;
    }

    private void DrawNight(Graphics g, MapProjector proj, DateTime utc)
    {
        if (_subSolar is null || (utc - _subSolarTime).Duration() > TimeSpan.FromMinutes(2))
        {
            try
            {
                var sun = Sun.Predict(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToGeodetic();
                _subSolar = new GeoPoint(sun.Latitude.Degrees, GeoMath.Wrap180(sun.Longitude.Degrees));
                _subSolarTime = utc;
            }
            catch
            {
                return;
            }
        }

        var anti = new GeoPoint(-_subSolar.Value.Latitude, GeoMath.Wrap180(_subSolar.Value.Longitude + 180));
        using var night = new SolidBrush(_palette.Night);
        proj.DrawSmallCircle(g, night, null, anti, 90);
    }

    private void DrawSatellite(Graphics g, MapProjector proj, TrackerSnapshot snap, float k)
    {
        var target = _palette.Target;

        using (var past = new Pen(Color.FromArgb(170, _palette.TrackPast), 1.5f * k))
            proj.DrawPolyline(g, past, snap.TrackPast);

        using (var future = new Pen(Color.FromArgb(210, target), 1.5f * k) { DashStyle = DashStyle.Dash })
            proj.DrawPolyline(g, future, snap.TrackFuture);

        if (snap.SubPoint is not { } sub) return;

        double footprint = GeoMath.FootprintRadiusDeg(sub.AltitudeKm);
        using (var fill = new SolidBrush(Color.FromArgb(_palette.IsDark ? 26 : 34, target)))
        using (var edge = new Pen(Color.FromArgb(150, target), 1.2f * k))
            proj.DrawSmallCircle(g, fill, edge, sub.Point, footprint);

        // In planar mode, the line from the centre to the satellite is the bearing to it.
        if (proj is PlanarProjector pp && proj.TryPoint(sub.Latitude, sub.Longitude, out var spt))
        {
            using var bearing = new Pen(Color.FromArgb(120, target), 1f * k) { DashStyle = DashStyle.Dot };
            g.DrawLine(bearing, pp.Center, spt);
        }

        if (proj.TryPoint(sub.Latitude, sub.Longitude, out var p))
        {
            float r = 5f * k;
            using var halo = new SolidBrush(Color.FromArgb(70, target));
            using var dot = new SolidBrush(target);
            using var ring = new Pen(_palette.Window, 1.5f * k);
            g.FillEllipse(halo, p.X - r * 2, p.Y - r * 2, r * 4, r * 4);
            g.FillEllipse(dot, p.X - r, p.Y - r, r * 2, r * 2);
            g.DrawEllipse(ring, p.X - r, p.Y - r, r * 2, r * 2);

            if (snap.SatelliteName is { } name)
            {
                var font = Fonts.Get(12f * k, true);
                DrawLabel(g, name, font, target, new PointF(p.X + r * 2, p.Y - r * 2 - font.Height / 2f));
            }
        }
    }

    private void DrawTraces(Graphics g, MapProjector proj, float k)
    {
        var traces = Traces;
        if (traces.Count == 0) return;

        using var faint = new Pen(Color.FromArgb(_palette.IsDark ? 110 : 140, _palette.TrackPast), 1f * k);
        using var bold = new Pen(_palette.Target, 2.2f * k) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
        using var dot = new SolidBrush(_palette.Target);

        foreach (var t in traces)
            if (!t.Highlight) proj.DrawPolyline(g, faint, t.Points);

        var font = Fonts.Get(11f * k, true);
        foreach (var t in traces)
        {
            if (!t.Highlight || t.Points.Count == 0) continue;
            proj.DrawPolyline(g, bold, t.Points);

            // Dot and label where the pass starts (the rise end of the track), away from the
            // station, where high passes would otherwise stack their labels.
            var start = t.Points[0];
            if (proj.TryPoint(start.Latitude, start.Longitude, out var sp))
            {
                g.FillEllipse(dot, sp.X - 3 * k, sp.Y - 3 * k, 6 * k, 6 * k);
                if (t.Label is not null)
                {
                    var size = g.MeasureString(t.Label, font);
                    var next = t.Points[Math.Min(3, t.Points.Count - 1)];
                    // Put the label on the side away from the direction of travel.
                    float dx = 0;
                    if (proj.TryPoint(next.Latitude, next.Longitude, out var np)) dx = np.X - sp.X;
                    float x = dx > 0 ? sp.X - size.Width - 4 * k : sp.X + 6 * k;
                    DrawLabel(g, t.Label, font, _palette.Target, new PointF(x, sp.Y - size.Height / 2));
                }
            }
        }
    }

    private void DrawStation(Graphics g, PointF p, string grid, float k)
    {
        float r = 5f * k;
        var diamond = new[]
        {
            new PointF(p.X, p.Y - r), new PointF(p.X + r, p.Y),
            new PointF(p.X, p.Y + r), new PointF(p.X - r, p.Y),
        };
        using var fill = new SolidBrush(_palette.Station);
        using var edge = new Pen(_palette.Window, 1.5f * k);
        g.FillPolygon(fill, diamond);
        g.DrawPolygon(edge, diamond);

        var font = Fonts.Get(10.5f * k);
        DrawLabel(g, grid, font, _palette.Station, new PointF(p.X + r * 1.6f, p.Y + r * 0.4f));
    }

    private void DrawLabel(Graphics g, string text, Font font, Color color, PointF at)
    {
        // A soft outline in the window color keeps labels readable over land and night shading.
        using var shadow = new SolidBrush(Color.FromArgb(200, _palette.Window));
        using var brush = new SolidBrush(color);
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
                if (dx != 0 || dy != 0) g.DrawString(text, font, shadow, at.X + dx, at.Y + dy);
        g.DrawString(text, font, brush, at);
    }

    private void DrawInfo(Graphics g, TrackerSnapshot? snap, int w, int h, float k)
    {
        float big = Math.Clamp(h * 0.065f, 13f * k, 20f * k);
        float small = Math.Clamp(h * 0.048f, 11f * k, 15f * k);
        var titleFont = Fonts.Get(big, true);
        var bodyFont = Fonts.Get(small);

        var lines = new List<(string text, Font font, Color color)>();

        if (snap?.SatelliteName is { } name)
        {
            lines.Add((name, titleFont, _palette.Text));

            if (snap.Look is { } look)
            {
                string up = look.Elevation >= 0 ? "" : "  (below horizon)";
                lines.Add(($"Az {look.Azimuth:0.0}°   El {look.Elevation:0.0}°   {look.RangeKm:#,0} km{up}", bodyFont,
                           look.Elevation >= 0 ? _palette.Target : _palette.TextDim));
            }

            if (snap.Pass is { } pass)
            {
                string text;
                if (pass.NeverSets)
                    text = "Always above your horizon";
                else if (pass.IsInProgress(snap.UtcNow))
                    text = $"Up now, max {pass.MaxElevation:0}° at {pass.MaxElevationUtc:HH:mm}Z, sets {pass.LosUtc:HH:mm:ss}Z";
                else
                    text = $"Rises {pass.AosUtc:HH:mm}Z at az {pass.AosAzimuth:0}°, max {pass.MaxElevation:0}°, sets {pass.LosUtc:HH:mm}Z";
                if (snap.PassUsesFlip) text += ", flip";
                lines.Add((text, bodyFont, _palette.Text));
            }
        }
        else
        {
            lines.Add(("No satellite selected", titleFont, _palette.TextDim));
        }

        if (!string.IsNullOrEmpty(snap?.Activity))
            lines.Add((snap.Activity, bodyFont, _palette.TextDim));

        float pad = 6f * k;
        float maxW = Math.Max(120 * k, w * 0.62f);
        float y = pad * 1.5f, boxW = 0;
        var measured = lines.Select(l =>
        {
            var size = g.MeasureString(l.text, l.font, (int)maxW);
            boxW = Math.Max(boxW, size.Width);
            return size;
        }).ToList();
        float boxH = measured.Sum(s => s.Height) + pad * 2;

        using (var bg = new SolidBrush(Color.FromArgb(_palette.IsDark ? 200 : 215, _palette.Panel)))
            g.FillRectangle(bg, pad, pad, boxW + pad * 2, boxH);

        for (int i = 0; i < lines.Count; i++)
        {
            using var b = new SolidBrush(lines[i].color);
            g.DrawString(lines[i].text, lines[i].font, b, new RectangleF(pad * 2, y, maxW, measured[i].Height + 2));
            y += measured[i].Height;
        }
    }

    private void DrawChips(Graphics g, int w, int h, float k)
    {
        var font = Fonts.Get(Math.Clamp(h * 0.042f, 10.5f * k, 13f * k));
        float pad = 6f * k, chipH = font.Height + 6f * k;

        Rectangle[] Layout(string[] labels, float right, float bottom)
        {
            var widths = labels.Select(l => g.MeasureString(l, font).Width + 10f * k).ToArray();
            float x = right - widths.Sum();
            var rects = new Rectangle[labels.Length];
            for (int i = 0; i < labels.Length; i++)
            {
                rects[i] = Rectangle.Round(new RectangleF(x, bottom - chipH, widths[i], chipH));
                x += widths[i];
            }
            return rects;
        }

        var projLabels = new[] { "Mercator", "Planar" };
        var themeLabels = new[] { "Dark", "Light" };
        if (ShowThemeChips)
        {
            _themeChips = Layout(themeLabels, w - pad, h - pad);
            _projChips = Layout(projLabels, _themeChips[0].Left - pad, h - pad);
            DrawSegments(g, _themeChips, themeLabels, _palette.IsDark ? 0 : 1, font, k);
        }
        else
        {
            _themeChips = Array.Empty<Rectangle>();
            _projChips = Layout(projLabels, w - pad, h - pad);
        }
        DrawSegments(g, _projChips, projLabels, _projection == MapProjection.Mercator ? 0 : 1, font, k);
    }

    private void DrawSegments(Graphics g, Rectangle[] rects, string[] labels, int selected, Font font, float k)
    {
        var all = Rectangle.Union(rects[0], rects[^1]);
        using var bg = new SolidBrush(Color.FromArgb(220, _palette.Panel));
        using var sel = new SolidBrush(Color.FromArgb(_palette.IsDark ? 90 : 60, _palette.Rotator));
        using var edge = new Pen(_palette.PanelEdge, 1f * k);
        using var on = new SolidBrush(_palette.Text);
        using var off = new SolidBrush(_palette.TextDim);
        var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        g.FillRectangle(bg, all);
        for (int i = 0; i < rects.Length; i++)
        {
            if (i == selected) g.FillRectangle(sel, rects[i]);
            g.DrawString(labels[i], font, i == selected ? on : off, rects[i], fmt);
        }
        g.DrawRectangle(edge, all);
        for (int i = 1; i < rects.Length; i++)
            g.DrawLine(edge, rects[i].Left, rects[i].Top, rects[i].Left, rects[i].Bottom);
    }
}
