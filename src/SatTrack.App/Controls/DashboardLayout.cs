namespace SatTrack.App.Controls;

/// <summary>Pure size arithmetic for <see cref="DashboardLayout"/>, kept separate so it can be tested.</summary>
public static class DashboardMath
{
    public readonly record struct Box(int X, int Y, int Width, int Height);

    /// <summary>
    /// Splits the client area into four boxes: map (top left, or the whole top when the frequency
    /// panel is hidden), frequency panel (top right), azimuth gauge and elevation gauge (bottom halves).
    /// Depends only on the client size, so the result is the same whether the window grew or shrank.
    /// </summary>
    public static (Box Map, Box Frequency, Box Azimuth, Box Elevation) Compute(
        int clientWidth, int clientHeight, bool showFrequency, int gap, int padding)
    {
        int x0 = padding, y0 = padding;
        int w = Math.Max(0, clientWidth - 2 * padding);
        int h = Math.Max(0, clientHeight - 2 * padding);
        int g = Math.Min(gap, Math.Min(w, h));

        int topH = Math.Max(0, (h - g) / 2);
        int bottomY = y0 + topH + g;
        int bottomH = Math.Max(0, h - topH - g);

        Box map, freq;
        if (showFrequency)
        {
            int mapW = Math.Max(0, (w - g) / 2);
            map = new Box(x0, y0, mapW, topH);
            freq = new Box(x0 + mapW + g, y0, Math.Max(0, w - mapW - g), topH);
        }
        else
        {
            map = new Box(x0, y0, w, topH);
            freq = new Box(x0, y0, 0, 0);
        }

        int azW = Math.Max(0, (w - g) / 2);
        var az = new Box(x0, bottomY, azW, bottomH);
        var el = new Box(x0 + azW + g, bottomY, Math.Max(0, w - azW - g), bottomH);
        return (map, freq, az, el);
    }
}

/// <summary>
/// Holds the map, frequency panel and the two gauges and sets their bounds directly from its
/// own size on every layout. Unlike a TableLayoutPanel it never asks the children for a
/// preferred size, so the gauges always shrink back when the window gets smaller.
/// </summary>
public sealed class DashboardLayout : Control
{
    private readonly Control _map, _freq, _az, _el;
    private bool _showFrequency = true;

    public int Gap { get; set; } = 4;
    public int Edge { get; set; } = 3;

    public DashboardLayout(Control map, Control frequency, Control azimuth, Control elevation)
    {
        _map = map; _freq = frequency; _az = azimuth; _el = elevation;
        foreach (var c in new[] { map, frequency, azimuth, elevation })
        {
            c.Dock = DockStyle.None;
            c.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            c.Margin = Padding.Empty;
            c.MinimumSize = Size.Empty;
            Controls.Add(c);
        }
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        TabStop = false;
    }

    public bool ShowFrequency
    {
        get => _showFrequency;
        set
        {
            if (_showFrequency == value) return;
            _showFrequency = value;
            _freq.Visible = value;
            PerformLayout();
        }
    }

    /// <summary>Never report a preferred size: the parent decides how big this is.</summary>
    public override Size GetPreferredSize(Size proposedSize) => new(1, 1);

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        PerformLayout();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var (map, freq, az, el) = DashboardMath.Compute(ClientSize.Width, ClientSize.Height, _showFrequency, Gap, Edge);
        Place(_map, map);
        if (_showFrequency) Place(_freq, freq);
        Place(_az, az);
        Place(_el, el);
    }

    private static void Place(Control c, DashboardMath.Box b)
    {
        var r = new Rectangle(b.X, b.Y, b.Width, b.Height);
        if (c.Bounds != r) c.Bounds = r;
    }
}
