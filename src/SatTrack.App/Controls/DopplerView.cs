using System.Drawing.Drawing2D;
using SatTrack.Core.Radio;
using SatTrack.Core.Tracking;

namespace SatTrack.App.Controls;

/// <summary>
/// Frequency readouts (Doppler shift, corrected receive/transmit, radio) and a chart of the
/// downlink Doppler shift across the pass with a "now" marker.
/// </summary>
public sealed class DopplerView : Control
{
    private Palette _palette = Palette.Dark;
    private RadioSnapshot? _radio;
    private TrackerSnapshot? _tracker;
    private IReadOnlyList<(DateTime Utc, double RangeRateKmS, double Elevation)> _series =
        Array.Empty<(DateTime, double, double)>();

    public DopplerView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        TabStop = false;
    }

    public Palette Palette { get => _palette; set { _palette = value; Invalidate(); } }

    public void SetData(RadioSnapshot radio, TrackerSnapshot tracker)
    {
        _radio = radio;
        _tracker = tracker;
        Invalidate();
    }

    public IReadOnlyList<(DateTime Utc, double RangeRateKmS, double Elevation)> Series
    {
        get => _series;
        set { _series = value; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        float k = g.DpiX / 96f;
        int w = ClientSize.Width, h = ClientSize.Height;
        g.Clear(_palette.Panel);
        if (w < 60 || h < 40) return;

        var radio = _radio;
        float pad = 6f * k;

        // ---- readouts
        var items = new List<(string label, string value, Color color)>();
        double? rate = DopplerRate();
        string rateText = rate is double r ? $"  {r:+0;-0} Hz/s" : "";
        items.Add(("Doppler" + rateText, FormatShift(radio?.DownlinkShiftHz), _palette.Target));
        items.Add(("Receive", FormatMHz(radio?.DownlinkHz), _palette.Target));
        if (radio?.NominalUplinkHz is not null)
            items.Add(("Transmit", FormatMHz(radio.UplinkHz), _palette.Text));
        items.Add(("Radio", radio?.RadioHz is long rh ? FormatMHz(rh) : "—", _palette.Rotator));

        int cols = w >= 430 * k ? items.Count : 2;
        int rows = (items.Count + cols - 1) / cols;
        float colW = (w - 2 * pad) / cols;
        float valuePx = Math.Clamp(Math.Min(h * 0.11f, colW * 0.12f), 11f * k, 24f * k);
        float labelPx = Math.Clamp(valuePx * 0.55f, 9f * k, 13f * k);
        float rowH = labelPx * 1.25f + valuePx * 1.3f;

        using var dim = new SolidBrush(_palette.TextDim);
        var fmt = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter };
        for (int i = 0; i < items.Count; i++)
        {
            float x = pad + (i % cols) * colW, y = pad * 0.5f + (i / cols) * rowH;
            using var vb = new SolidBrush(items[i].color);
            g.DrawString(items[i].label, Fonts.Get(labelPx), dim, new RectangleF(x, y, colW, labelPx * 1.4f), fmt);
            g.DrawString(items[i].value, Fonts.Get(valuePx, true), vb, new RectangleF(x, y + labelPx * 1.2f, colW, valuePx * 1.4f), fmt);
        }

        float top = pad * 0.5f + rows * rowH + 2 * k;
        float actPx = Math.Clamp(h * 0.06f, 9.5f * k, 12f * k);
        if (!string.IsNullOrEmpty(radio?.Activity))
        {
            g.DrawString(radio.Activity, Fonts.Get(actPx), dim, new RectangleF(pad, top, w - 2 * pad, actPx * 1.5f), fmt);
            top += actPx * 1.5f;
        }

        // ---- chart
        var chart = new RectangleF(pad, top + 2 * k, w - 2 * pad, h - top - pad - 2 * k);
        if (chart.Height < 30 * k) return;
        DrawChart(g, chart, k);
    }

    private void DrawChart(Graphics g, RectangleF r, float k)
    {
        using var bg = new SolidBrush(_palette.Window);
        using var edge = new Pen(_palette.PanelEdge, 1f);
        g.FillRectangle(bg, r);

        float labelPx = Math.Clamp(r.Height * 0.11f, 9f * k, 11.5f * k);
        var font = Fonts.Get(labelPx);
        using var dim = new SolidBrush(_palette.TextDim);

        long? nominal = _radio?.NominalDownlinkHz;
        var series = _series;
        if (nominal is null || series.Count < 2)
        {
            var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            string msg = nominal is null ? "Enter a downlink frequency to see the Doppler curve" : "No pass data";
            g.DrawString(msg, font, dim, r, center);
            g.DrawRectangle(edge, r.X, r.Y, r.Width, r.Height);
            return;
        }

        var shifts = series.Select(p => Doppler.DownlinkShift(nominal.Value, p.RangeRateKmS)).ToArray();
        double maxAbs = Math.Max(1, shifts.Max(Math.Abs)) * 1.1;
        DateTime t0 = series[0].Utc, t1 = series[^1].Utc;
        double span = (t1 - t0).TotalSeconds;

        var plot = new RectangleF(r.X + 2, r.Y + labelPx * 0.4f, r.Width - 4, r.Height - labelPx * 1.9f);
        float X(DateTime t) => plot.X + (float)((t - t0).TotalSeconds / span) * plot.Width;
        float Y(double hz) => plot.Y + plot.Height / 2 - (float)(hz / maxAbs) * plot.Height / 2;

        // Shade the time the satellite is above the horizon.
        using (var up = new SolidBrush(Color.FromArgb(_palette.IsDark ? 22 : 30, _palette.Target)))
        {
            int i = 0;
            while (i < series.Count)
            {
                while (i < series.Count && series[i].Elevation <= 0) i++;
                int j = i;
                while (j < series.Count && series[j].Elevation > 0) j++;
                if (j > i)
                {
                    float xa = X(series[i].Utc), xb = X(series[Math.Min(j, series.Count - 1)].Utc);
                    g.FillRectangle(up, xa, plot.Y, Math.Max(1, xb - xa), plot.Height);
                }
                i = j;
            }
        }

        using (var zero = new Pen(_palette.PanelEdge, 1f * k))
            g.DrawLine(zero, plot.Left, Y(0), plot.Right, Y(0));

        var pts = series.Select((p, i) => new PointF(X(p.Utc), Y(shifts[i]))).ToArray();
        using (var line = new Pen(_palette.Target, 1.8f * k))
            g.DrawLines(line, pts);

        // Now marker.
        var now = _tracker?.UtcNow ?? DateTime.UtcNow;
        if (now >= t0 && now <= t1 && _radio?.DownlinkShiftHz is double cur)
        {
            float xn = X(now);
            using var nowPen = new Pen(_palette.Rotator, 1.2f * k) { DashStyle = DashStyle.Dash };
            g.DrawLine(nowPen, xn, plot.Top, xn, plot.Bottom);
            float rr = 3.5f * k;
            using var dot = new SolidBrush(_palette.Rotator);
            g.FillEllipse(dot, xn - rr, Y(cur) - rr, 2 * rr, 2 * rr);
        }

        // Labels: scale on the left, times along the bottom.
        g.DrawString(FormatShift(maxAbs / 1.1), font, dim, plot.X + 2, plot.Y);
        g.DrawString(FormatShift(-maxAbs / 1.1), font, dim, plot.X + 2, plot.Bottom - labelPx * 1.3f);
        float by = plot.Bottom + 1;
        g.DrawString($"{t0:HH:mm}Z", font, dim, plot.X, by);
        var right = new StringFormat { Alignment = StringAlignment.Far };
        g.DrawString($"{t1:HH:mm}Z", font, dim, new RectangleF(plot.X, by, plot.Width, labelPx * 1.5f), right);

        g.DrawRectangle(edge, r.X, r.Y, r.Width, r.Height);
    }

    /// <summary>Rate of change of the downlink Doppler at the current time, from the series.</summary>
    private double? DopplerRate()
    {
        var s = _series;
        var nominal = _radio?.NominalDownlinkHz;
        var now = _tracker?.UtcNow;
        if (nominal is null || now is null || s.Count < 2) return null;
        for (int i = 1; i < s.Count; i++)
        {
            if (s[i].Utc < now) continue;
            double dt = (s[i].Utc - s[i - 1].Utc).TotalSeconds;
            if (dt <= 0) return null;
            return (Doppler.DownlinkShift(nominal.Value, s[i].RangeRateKmS) -
                    Doppler.DownlinkShift(nominal.Value, s[i - 1].RangeRateKmS)) / dt;
        }
        return null;
    }

    public static string FormatMHz(double? hz)
    {
        if (hz is not double v) return "—";
        long h = (long)Math.Round(v);
        string mhz = (h / 1_000_000).ToString();
        string rest = (h % 1_000_000).ToString("000000");
        return $"{mhz}.{rest[..3]} {rest[3..]}";
    }

    public static string FormatShift(double? hz)
    {
        if (hz is not double v) return "—";
        return Math.Abs(v) >= 10_000 ? $"{v / 1000:+0.00;-0.00} kHz" : $"{v:+0;-0} Hz";
    }
}
