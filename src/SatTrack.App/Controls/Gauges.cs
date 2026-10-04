using System.Drawing.Drawing2D;
using SatTrack.Core.Geo;
using SatTrack.Core.Tracking;

namespace SatTrack.App.Controls;

/// <summary>A labelled value shown beside a dial.</summary>
public readonly record struct Readout(string Label, string Value, Color Color);

/// <summary>
/// Shared layout for the two gauges: dial on the left and readouts on the right when there's
/// room, or dial on top with readouts in a row underneath when the panel is tall and narrow.
/// Everything is sized from the control's size so it stays legible when the window is small.
/// </summary>
public abstract class GaugeBase : Control
{
    private Palette _palette = Palette.Dark;
    private TrackerSnapshot? _snapshot;

    protected GaugeBase()
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

    public TrackerSnapshot? Snapshot
    {
        get => _snapshot;
        set { _snapshot = value; Invalidate(); }
    }

    protected abstract string Title { get; }

    /// <summary>Width/height ratio the dial wants (1 for a circle, 2 for a half circle).</summary>
    protected abstract float DialAspect { get; }

    protected abstract void DrawDial(Graphics g, RectangleF box, float k);

    protected abstract IReadOnlyList<Readout> GetReadouts();

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        float k = g.DpiX / 96f;

        int w = ClientSize.Width, h = ClientSize.Height;
        g.Clear(Palette.Panel);
        if (w < 40 || h < 40) return;

        using (var edge = new Pen(Palette.PanelEdge, 1f))
            g.DrawRectangle(edge, 0, 0, w - 1, h - 1);

        float pad = Math.Max(6f * k, Math.Min(w, h) * 0.04f);
        float titlePx = Math.Clamp(h * 0.065f, 10.5f * k, 15f * k);
        using (var dim = new SolidBrush(Palette.TextDim))
            g.DrawString(Title, Fonts.Get(titlePx), dim, pad * 0.6f, pad * 0.4f);

        float top = pad * 0.4f + titlePx * 1.35f;
        var area = new RectangleF(pad, top, w - 2 * pad, h - top - pad);
        var readouts = GetReadouts();

        bool wide = area.Width >= area.Height * (DialAspect + 0.75f);
        if (wide)
        {
            float dialH = area.Height;
            float dialW = dialH * DialAspect;
            if (dialW > area.Width * 0.62f) { dialW = area.Width * 0.62f; dialH = dialW / DialAspect; }
            var dial = new RectangleF(area.X, area.Y + (area.Height - dialH) / 2, dialW, dialH);
            DrawDial(g, dial, k);

            var ro = new RectangleF(dial.Right + pad * 1.5f, area.Y, area.Right - dial.Right - pad * 1.5f, area.Height);
            DrawReadoutsColumn(g, ro, readouts, k);
        }
        else
        {
            float roH = Math.Clamp(area.Height * 0.3f, 30f * k, 70f * k);
            float dialH = area.Height - roH - pad * 0.5f;
            float dialW = Math.Min(area.Width, dialH * DialAspect);
            dialH = dialW / DialAspect;
            var dial = new RectangleF(area.X + (area.Width - dialW) / 2, area.Y, dialW, dialH);
            DrawDial(g, dial, k);

            var ro = new RectangleF(area.X, area.Bottom - roH, area.Width, roH);
            DrawReadoutsRow(g, ro, readouts, k);
        }
    }

    private void DrawReadoutsColumn(Graphics g, RectangleF r, IReadOnlyList<Readout> items, float k)
    {
        if (items.Count == 0 || r.Width < 30) return;
        float rowH = r.Height / items.Count;
        float valuePx = Math.Clamp(Math.Min(rowH * 0.5f, r.Width * 0.2f), 12f * k, 40f * k);
        float labelPx = Math.Clamp(valuePx * 0.42f, 9.5f * k, 15f * k);

        for (int i = 0; i < items.Count; i++)
        {
            float y = r.Y + i * rowH + (rowH - (valuePx + labelPx) * 1.2f) / 2;
            DrawReadout(g, items[i], r.X, y, r.Width, labelPx, valuePx);
        }
    }

    private void DrawReadoutsRow(Graphics g, RectangleF r, IReadOnlyList<Readout> items, float k)
    {
        if (items.Count == 0) return;
        float colW = r.Width / items.Count;
        float valuePx = Math.Clamp(Math.Min(r.Height * 0.48f, colW * 0.22f), 11f * k, 30f * k);
        float labelPx = Math.Clamp(valuePx * 0.45f, 9f * k, 13f * k);
        for (int i = 0; i < items.Count; i++)
            DrawReadout(g, items[i], r.X + i * colW, r.Y, colW, labelPx, valuePx);
    }

    private void DrawReadout(Graphics g, Readout item, float x, float y, float width, float labelPx, float valuePx)
    {
        using var labelBrush = new SolidBrush(Palette.TextDim);
        using var valueBrush = new SolidBrush(item.Color);
        var fmt = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter };
        g.DrawString(item.Label, Fonts.Get(labelPx), labelBrush, new RectangleF(x, y, width, labelPx * 1.4f), fmt);
        g.DrawString(item.Value, Fonts.Get(valuePx, true), valueBrush,
                     new RectangleF(x, y + labelPx * 1.15f, width, valuePx * 1.35f), fmt);
    }

    // ---------------------------------------------------------------- helpers for dials

    protected static PointF Polar(PointF c, float r, double screenAngleDeg)
    {
        double a = screenAngleDeg * Math.PI / 180;
        return new PointF(c.X + r * (float)Math.Cos(a), c.Y + r * (float)Math.Sin(a));
    }

    protected void DrawNeedle(Graphics g, PointF c, float length, double screenAngle, Color color, float width, bool arrow)
    {
        var tip = Polar(c, length, screenAngle);
        using var pen = new Pen(color, width) { StartCap = LineCap.Round, EndCap = arrow ? LineCap.Flat : LineCap.Round };
        g.DrawLine(pen, c, tip);
        if (arrow)
        {
            float s = width * 3.2f;
            var a = Polar(tip, s, screenAngle);
            var b = Polar(tip, s * 0.8f, screenAngle + 90);
            var d = Polar(tip, s * 0.8f, screenAngle - 90);
            using var brush = new SolidBrush(color);
            g.FillPolygon(brush, new[] { a, b, d });
        }
    }

    protected void DrawSatMarker(Graphics g, PointF p, float r, float k)
    {
        using var pen = new Pen(Palette.Target, 1.6f * k);
        using var fill = new SolidBrush(Palette.Panel);
        g.FillEllipse(fill, p.X - r, p.Y - r, 2 * r, 2 * r);
        g.DrawEllipse(pen, p.X - r, p.Y - r, 2 * r, 2 * r);
    }

    protected static string Deg(double? v, string format = "0") =>
        v is double d ? d.ToString(format) + "°" : "—";
}

/// <summary>Compass dial: rotator heading, commanded heading and the satellite's bearing.</summary>
public sealed class AzimuthGauge : GaugeBase
{
    protected override string Title => "Azimuth";
    protected override float DialAspect => 1f;

    protected override void DrawDial(Graphics g, RectangleF box, float k)
    {
        var snap = Snapshot;
        float R = Math.Min(box.Width, box.Height) / 2 - 1;
        var c = new PointF(box.X + box.Width / 2, box.Y + box.Height / 2);

        using (var face = new SolidBrush(Palette.Window))
            g.FillEllipse(face, c.X - R, c.Y - R, 2 * R, 2 * R);

        // Overlap zone of a 450° rotator: these bearings can be reached two ways.
        double maxAz = snap?.Limits.MaxAzimuth ?? 360;
        if (maxAz > 360)
        {
            float band = R * 0.09f;
            using var overlap = new Pen(Color.FromArgb(Palette.IsDark ? 70 : 55, Palette.Rotator), band);
            float rr = R - band / 2;
            g.DrawArc(overlap, c.X - rr, c.Y - rr, 2 * rr, 2 * rr, -90, (float)(maxAz - 360));
        }

        using (var edge = new Pen(Palette.PanelEdge, 1.5f * k))
            g.DrawEllipse(edge, c.X - R, c.Y - R, 2 * R, 2 * R);

        // Ticks and labels.
        using var tick = new Pen(Palette.TextDim, 1f * k);
        using var major = new Pen(Palette.Text, 1.6f * k);
        for (int b = 0; b < 360; b += 10)
        {
            bool isMajor = b % 30 == 0;
            float inner = R * (isMajor ? 0.86f : 0.92f);
            g.DrawLine(isMajor ? major : tick, Polar(c, inner, b - 90), Polar(c, R, b - 90));
        }

        float labelPx = Math.Max(9f * k, R * 0.13f);
        bool showNumbers = R > 55 * k;
        using var text = new SolidBrush(Palette.Text);
        using var dim = new SolidBrush(Palette.TextDim);
        var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        for (int b = 0; b < 360; b += 30)
        {
            string? label = b switch { 0 => "N", 90 => "E", 180 => "S", 270 => "W", _ => showNumbers ? b.ToString() : null };
            if (label is null) continue;
            bool cardinal = b % 90 == 0;
            var p = Polar(c, R * 0.70f, b - 90);
            g.DrawString(label, Fonts.Get(cardinal ? labelPx * 1.1f : labelPx * 0.8f, cardinal), cardinal ? text : dim, p, fmt);
        }

        if (snap is null) return;

        // Satellite bearing (where it is in the sky).
        if (snap.Look is { } look && look.Elevation > -5)
            DrawSatMarker(g, Polar(c, R * 0.95f, look.Azimuth - 90), Math.Max(3.5f * k, R * 0.05f), k);

        if (snap.Target is { } t)
            DrawNeedle(g, c, R * 0.84f, GeoMath.Wrap360(t.Azimuth) - 90, Palette.Target, Math.Max(1.6f * k, R * 0.018f), arrow: true);

        if (snap.RotatorPosition is { } rp)
        {
            DrawNeedle(g, c, R * 0.62f, GeoMath.Wrap360(rp.Azimuth) - 90, Palette.Rotator, Math.Max(3f * k, R * 0.045f), arrow: false);
            float hub = Math.Max(3f * k, R * 0.05f);
            using var hb = new SolidBrush(Palette.Rotator);
            g.FillEllipse(hb, c.X - hub, c.Y - hub, 2 * hub, 2 * hub);
        }
    }

    protected override IReadOnlyList<Readout> GetReadouts()
    {
        var s = Snapshot;
        var rot = s?.RotatorPosition?.Azimuth;
        string rotLabel = rot is >= 360 ? $"Rotator ({rot:0}° on controller)" : "Rotator";
        return new[]
        {
            new Readout(rotLabel, Deg(rot is double r ? GeoMath.Wrap360(r) : null), Palette.Rotator),
            new Readout("Target", Deg(s?.Target is { } t ? GeoMath.Wrap360(t.Azimuth) : null, "0.0"), Palette.Target),
            new Readout("Satellite", Deg(s?.Look?.Azimuth, "0.0"), Palette.Text),
        };
    }
}

/// <summary>
/// Elevation dial: a quarter circle for 0-90° rotators, a half circle when the rotator can
/// go to 180° (flip mode), so a flipped antenna shows on the far side.
/// </summary>
public sealed class ElevationGauge : GaugeBase
{
    protected override string Title => "Elevation";

    private double MaxEl => Snapshot?.Limits.MaxElevation ?? 90;
    private bool Half => MaxEl > 90;

    protected override float DialAspect => Half ? 2f : 1f;

    protected override void DrawDial(Graphics g, RectangleF box, float k)
    {
        var snap = Snapshot;
        // Leave room for the needle hub below the horizon line and beside the zenith line.
        float hubRoom = Math.Max(4f * k, Math.Min(box.Width, box.Height) * 0.06f);
        float R;
        PointF c;
        if (Half)
        {
            R = Math.Min(box.Width / 2 - 2, box.Height - hubRoom);
            c = new PointF(box.X + box.Width / 2, box.Y + (box.Height - hubRoom + R) / 2);
        }
        else
        {
            R = Math.Min(box.Width - hubRoom, box.Height - hubRoom);
            c = new PointF(box.X + hubRoom + (box.Width - hubRoom - R) / 2, box.Y + (box.Height - hubRoom + R) / 2);
        }

        float sweep = Half ? 180 : 90;
        using (var face = new SolidBrush(Palette.Window))
            g.FillPie(face, c.X - R, c.Y - R, 2 * R, 2 * R, -sweep, sweep);
        using (var edge = new Pen(Palette.PanelEdge, 1.5f * k))
        {
            g.DrawArc(edge, c.X - R, c.Y - R, 2 * R, 2 * R, -sweep, sweep);
            g.DrawLine(edge, Half ? c.X - R : c.X, c.Y, c.X + R, c.Y);
            if (!Half) g.DrawLine(edge, c.X, c.Y, c.X, c.Y - R);
        }

        // Elevation e is drawn at screen angle -e (0 = horizon to the right, 90 = straight up).
        using var tick = new Pen(Palette.TextDim, 1f * k);
        using var major = new Pen(Palette.Text, 1.6f * k);
        for (int e = 0; e <= (int)sweep; e += 10)
        {
            bool isMajor = e % 30 == 0;
            g.DrawLine(isMajor ? major : tick, Polar(c, R * (isMajor ? 0.88f : 0.93f), -e), Polar(c, R, -e));
        }

        float labelPx = Math.Max(9f * k, R * 0.1f);
        using var dim = new SolidBrush(Palette.TextDim);
        var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        for (int e = 0; e <= (int)sweep; e += 30)
        {
            if (R < 50 * k && e % 90 != 0) continue;
            var p = Polar(c, R * 0.76f, -e);
            if (e == 0 || e == 180) p.Y -= labelPx * 0.6f;
            if (e == 90 && !Half) p.X += labelPx * 0.7f;
            g.DrawString(e.ToString(), Fonts.Get(labelPx), dim, p, fmt);
        }

        if (snap is null) return;

        if (snap.Look is { } look && look.Elevation > -5)
            DrawSatMarker(g, Polar(c, R * 0.95f, -Math.Max(0, look.Elevation)), Math.Max(3.5f * k, R * 0.05f), k);

        if (snap.Target is { } t)
            DrawNeedle(g, c, R * 0.84f, -Math.Clamp(t.Elevation, 0, sweep), Palette.Target, Math.Max(1.6f * k, R * 0.018f), arrow: true);

        if (snap.RotatorPosition is { } rp)
        {
            DrawNeedle(g, c, R * 0.64f, -Math.Clamp(rp.Elevation, 0, sweep), Palette.Rotator, Math.Max(3f * k, R * 0.045f), arrow: false);
            float hub = Math.Max(3f * k, R * 0.05f);
            using var hb = new SolidBrush(Palette.Rotator);
            g.FillEllipse(hb, c.X - hub, c.Y - hub, 2 * hub, 2 * hub);
        }
    }

    protected override IReadOnlyList<Readout> GetReadouts()
    {
        var s = Snapshot;
        string targetLabel = s?.TargetFlipped == true ? "Target (flipped)" : "Target";
        return new[]
        {
            new Readout("Rotator", Deg(s?.RotatorPosition?.Elevation), Palette.Rotator),
            new Readout(targetLabel, Deg(s?.Target?.Elevation, "0.0"), Palette.Target),
            new Readout("Satellite", Deg(s?.Look?.Elevation, "0.0"), Palette.Text),
        };
    }
}
