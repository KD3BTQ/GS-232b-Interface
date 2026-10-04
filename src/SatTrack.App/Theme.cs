using System.Drawing.Text;

namespace SatTrack.App;

/// <summary>Colors for one theme. Map and gauges are drawn entirely from these.</summary>
public sealed class Palette
{
    public required string Name { get; init; }
    public required bool IsDark { get; init; }

    public required Color Window { get; init; }
    public required Color Panel { get; init; }
    public required Color PanelEdge { get; init; }
    public required Color Text { get; init; }
    public required Color TextDim { get; init; }

    public required Color Ocean { get; init; }
    public required Color Land { get; init; }
    public required Color Coast { get; init; }
    public required Color Border { get; init; }
    public required Color Graticule { get; init; }
    public required Color Night { get; init; }

    /// <summary>Where the rotator is pointing now.</summary>
    public required Color Rotator { get; init; }

    /// <summary>Where the rotator is being sent / the satellite.</summary>
    public required Color Target { get; init; }

    public required Color TrackPast { get; init; }
    public required Color Station { get; init; }
    public required Color Danger { get; init; }
    public required Color Good { get; init; }

    public static readonly Palette Dark = new()
    {
        Name = "Dark",
        IsDark = true,
        Window = Color.FromArgb(0x0F, 0x1B, 0x24),
        Panel = Color.FromArgb(0x13, 0x22, 0x2D),
        PanelEdge = Color.FromArgb(0x24, 0x3A, 0x48),
        Text = Color.FromArgb(0xD5, 0xE1, 0xE8),
        TextDim = Color.FromArgb(0x7D, 0x95, 0xA3),
        Ocean = Color.FromArgb(0x0E, 0x1A, 0x23),
        Land = Color.FromArgb(0x1F, 0x33, 0x3E),
        Coast = Color.FromArgb(0x3B, 0x58, 0x68),
        Border = Color.FromArgb(0x2A, 0x43, 0x51),
        Graticule = Color.FromArgb(0x19, 0x2C, 0x38),
        Night = Color.FromArgb(105, 0, 0, 0),
        Rotator = Color.FromArgb(0x4F, 0xC3, 0xE8),
        Target = Color.FromArgb(0xF2, 0xA9, 0x3B),
        TrackPast = Color.FromArgb(0x6A, 0x82, 0x90),
        Station = Color.FromArgb(0xE8, 0xEE, 0xF2),
        Danger = Color.FromArgb(0xE5, 0x48, 0x4D),
        Good = Color.FromArgb(0x46, 0xA7, 0x58),
    };

    public static readonly Palette Light = new()
    {
        Name = "Light",
        IsDark = false,
        Window = Color.FromArgb(0xEE, 0xF2, 0xF4),
        Panel = Color.FromArgb(0xF8, 0xFA, 0xFB),
        PanelEdge = Color.FromArgb(0xCF, 0xD9, 0xDF),
        Text = Color.FromArgb(0x1C, 0x2A, 0x33),
        TextDim = Color.FromArgb(0x5C, 0x6F, 0x7B),
        Ocean = Color.FromArgb(0xD6, 0xE5, 0xED),
        Land = Color.FromArgb(0xE8, 0xE6, 0xDB),
        Coast = Color.FromArgb(0x93, 0xA5, 0xA0),
        Border = Color.FromArgb(0xC2, 0xC6, 0xBC),
        Graticule = Color.FromArgb(0xC4, 0xD5, 0xDF),
        Night = Color.FromArgb(38, 0x10, 0x20, 0x40),
        Rotator = Color.FromArgb(0x0B, 0x7F, 0xAB),
        Target = Color.FromArgb(0xC7, 0x6A, 0x00),
        TrackPast = Color.FromArgb(0x84, 0x96, 0xA0),
        Station = Color.FromArgb(0x1C, 0x2A, 0x33),
        Danger = Color.FromArgb(0xC9, 0x30, 0x2C),
        Good = Color.FromArgb(0x2E, 0x7D, 0x32),
    };

    public static Palette For(bool dark) => dark ? Dark : Light;
}

/// <summary>Fonts. Bahnschrift (built into Windows 10/11) for readouts, Segoe UI fallback.</summary>
public static class Fonts
{
    private static readonly Dictionary<(int, bool), Font> Cache = new();
    private static string? _family;

    private static string Family
    {
        get
        {
            if (_family is not null) return _family;
            using var installed = new InstalledFontCollection();
            _family = installed.Families.Any(f => f.Name == "Bahnschrift") ? "Bahnschrift" : "Segoe UI";
            return _family;
        }
    }

    /// <summary>A font sized in pixels (cached; don't dispose).</summary>
    public static Font Get(float sizePx, bool bold = false)
    {
        int key = Math.Max(6, (int)Math.Round(sizePx));
        lock (Cache)
        {
            if (!Cache.TryGetValue((key, bold), out var f))
            {
                f = new Font(Family, key, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
                Cache[(key, bold)] = f;
            }
            return f;
        }
    }
}

/// <summary>Flat toolstrip/statusstrip renderer that follows the palette.</summary>
public sealed class FlatRenderer : ToolStripProfessionalRenderer
{
    private readonly Palette _p;

    public FlatRenderer(Palette p) : base(new FlatColors(p))
    {
        _p = p;
        RoundedEdges = false;
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        if (e.Item.Tag is not "keepcolor") e.TextColor = e.Item.Enabled ? _p.Text : _p.TextDim;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
    {
        if (e.Item is ToolStripButton { BackColor: var bc } btn && btn.Tag is "keepcolor" && bc != Color.Empty && bc.A > 0)
        {
            using var b = new SolidBrush(bc);
            e.Graphics.FillRectangle(b, new Rectangle(Point.Empty, e.Item.Size));
            if (btn.Selected)
            {
                using var pen = new Pen(_p.Text);
                e.Graphics.DrawRectangle(pen, 0, 0, e.Item.Width - 1, e.Item.Height - 1);
            }
            return;
        }
        base.OnRenderButtonBackground(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = _p.Text;
        base.OnRenderArrow(e);
    }

    private sealed class FlatColors : ProfessionalColorTable
    {
        private readonly Palette _p;
        public FlatColors(Palette p) { _p = p; UseSystemColors = false; }

        private Color Hover => Blend(_p.Panel, _p.Rotator, 0.18);
        private Color Press => Blend(_p.Panel, _p.Rotator, 0.32);
        private static Color Blend(Color a, Color b, double t) => Color.FromArgb(
            (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

        public override Color ToolStripGradientBegin => _p.Panel;
        public override Color ToolStripGradientMiddle => _p.Panel;
        public override Color ToolStripGradientEnd => _p.Panel;
        public override Color ToolStripBorder => _p.PanelEdge;
        public override Color ToolStripDropDownBackground => _p.Panel;
        public override Color ToolStripContentPanelGradientBegin => _p.Window;
        public override Color ToolStripContentPanelGradientEnd => _p.Window;
        public override Color StatusStripGradientBegin => _p.Panel;
        public override Color StatusStripGradientEnd => _p.Panel;
        public override Color MenuBorder => _p.PanelEdge;
        public override Color MenuItemBorder => _p.Rotator;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Press;
        public override Color MenuItemPressedGradientEnd => Press;
        public override Color ImageMarginGradientBegin => _p.Panel;
        public override Color ImageMarginGradientMiddle => _p.Panel;
        public override Color ImageMarginGradientEnd => _p.Panel;
        public override Color ButtonSelectedHighlight => Hover;
        public override Color ButtonSelectedGradientBegin => Hover;
        public override Color ButtonSelectedGradientMiddle => Hover;
        public override Color ButtonSelectedGradientEnd => Hover;
        public override Color ButtonSelectedBorder => _p.Rotator;
        public override Color ButtonPressedGradientBegin => Press;
        public override Color ButtonPressedGradientMiddle => Press;
        public override Color ButtonPressedGradientEnd => Press;
        public override Color ButtonPressedBorder => _p.Rotator;
        public override Color ButtonCheckedGradientBegin => Press;
        public override Color ButtonCheckedGradientMiddle => Press;
        public override Color ButtonCheckedGradientEnd => Press;
        public override Color ButtonCheckedHighlight => Press;
        public override Color CheckBackground => Press;
        public override Color CheckSelectedBackground => Press;
        public override Color CheckPressedBackground => Press;
        public override Color SeparatorDark => _p.PanelEdge;
        public override Color SeparatorLight => _p.Panel;
        public override Color GripDark => _p.PanelEdge;
        public override Color GripLight => _p.Panel;
        public override Color OverflowButtonGradientBegin => _p.Panel;
        public override Color OverflowButtonGradientMiddle => _p.Panel;
        public override Color OverflowButtonGradientEnd => _p.Panel;
    }
}
