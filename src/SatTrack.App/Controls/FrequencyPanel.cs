using System.Globalization;
using SatTrack.Core.Radio;
using SatTrack.Core.Tracking;

namespace SatTrack.App.Controls;

/// <summary>
/// Right-hand half of the top area: radio Connect / Enable / Track, the satellite's downlink
/// and repeater offset, and the Doppler readouts and chart.
/// </summary>
public sealed class FrequencyPanel : UserControl
{
    private Palette _palette = Palette.Dark;
    private bool _settingText;

    private readonly Label _title = new() { Text = "Radio", AutoSize = true, Margin = new Padding(2, 6, 8, 0) };
    private readonly Button _connect = MakeButton("Connect");
    private readonly Button _enable = MakeButton("Enable");
    private readonly Button _track = MakeButton("Track");

    private readonly Label _downLabel = new() { Text = "Downlink MHz", AutoSize = true, Margin = new Padding(2, 6, 4, 0) };
    private readonly TextBox _down = new() { Width = 100, TextAlign = HorizontalAlignment.Right, Margin = new Padding(0, 2, 10, 2) };
    private readonly Label _offLabel = new() { Text = "Offset MHz", AutoSize = true, Margin = new Padding(2, 6, 4, 0) };
    private readonly TextBox _offset = new() { Width = 90, TextAlign = HorizontalAlignment.Right, Margin = new Padding(0, 2, 2, 2) };
    private readonly ToolTip _tips = new();

    public DopplerView Doppler { get; } = new() { Dock = DockStyle.Fill, Margin = new Padding(0) };

    public event EventHandler? ConnectClicked;
    public event EventHandler? EnableClicked;
    public event EventHandler? TrackClicked;

    /// <summary>Raised when the user commits new frequencies (Enter or leaving the box).</summary>
    public event Action<long?, long>? FrequenciesChanged;

    public FrequencyPanel()
    {
        SuspendLayout();
        AutoScaleMode = AutoScaleMode.Inherit;
        Padding = new Padding(4, 2, 4, 4);

        var header = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
        header.Controls.AddRange(new Control[] { _title, _connect, _enable, _track });

        var inputs = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0) };
        inputs.Controls.AddRange(new Control[] { _downLabel, _down, _offLabel, _offset });

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(header, 0, 0);
        root.Controls.Add(inputs, 0, 1);
        root.Controls.Add(Doppler, 0, 2);
        Controls.Add(root);
        ResumeLayout(false);

        _tips.SetToolTip(_connect, "Connect to the radio and read its frequency");
        _tips.SetToolTip(_enable, "Allow the app to change the radio's frequency (Esc disarms)");
        _tips.SetToolTip(_track, "Keep the radio on the Doppler-corrected frequency");
        _tips.SetToolTip(_down, "The satellite's nominal downlink (what it transmits), in MHz");
        _tips.SetToolTip(_offset, "For repeaters/transponders: uplink minus downlink, in MHz. Leave empty if none.");

        _connect.Click += (s, e) => ConnectClicked?.Invoke(this, e);
        _enable.Click += (s, e) => EnableClicked?.Invoke(this, e);
        _track.Click += (s, e) => TrackClicked?.Invoke(this, e);

        foreach (var box in new[] { _down, _offset })
        {
            box.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Enter) { Commit(); e.SuppressKeyPress = true; }
            };
            box.Leave += (_, _) => Commit();
        }
    }

    public Palette Palette
    {
        get => _palette;
        set
        {
            _palette = value;
            BackColor = value.Panel;
            foreach (var l in new[] { _title, _downLabel, _offLabel }) l.ForeColor = value.TextDim;
            _title.ForeColor = value.Text;
            foreach (var b in new[] { _down, _offset })
            {
                b.BackColor = value.Window;
                b.ForeColor = value.Text;
                b.BorderStyle = BorderStyle.FixedSingle;
            }
            foreach (var b in new[] { _connect, _enable, _track }) StyleButton(b, false);
            Doppler.Palette = value;
            Invalidate(true);
        }
    }

    /// <summary>Fills the boxes (e.g. when the satellite changes) without raising FrequenciesChanged.</summary>
    public void SetFrequencies(long? downlinkHz, long offsetHz)
    {
        _settingText = true;
        _down.Text = downlinkHz is long d ? (d / 1e6).ToString("0.000###", CultureInfo.InvariantCulture) : "";
        _offset.Text = offsetHz != 0 ? (offsetHz / 1e6).ToString("0.000###", CultureInfo.InvariantCulture) : "";
        _down.ForeColor = _offset.ForeColor = _palette.Text;
        _settingText = false;
    }

    public void UpdateState(RadioSnapshot radio, TrackerSnapshot tracker, bool connecting)
    {
        SetText(_connect, connecting ? "Connecting…"
                       : radio.Connection == RotatorConnection.Connected ? "Disconnect"
                       : radio.Connection == RotatorConnection.Lost ? "Reconnect"
                       : "Connect");
        _connect.Enabled = !connecting;

        _enable.Enabled = radio.Connection == RotatorConnection.Connected || radio.Armed;
        SetText(_enable, radio.Armed ? "Disarm" : "Enable");
        StyleButton(_enable, radio.Armed);

        SetText(_track, radio.Tracking ? "Stop tracking" : "Track");
        _track.Enabled = radio.NominalDownlinkHz is not null || radio.Tracking;
        if (radio.Tracking) _track.BackColor = Blend(_palette.Panel, _palette.Rotator, 0.3);
        else StyleButton(_track, false);

        Doppler.SetData(radio, tracker);
    }

    private void Commit()
    {
        if (_settingText) return;
        bool ok = true;

        long? down = null;
        if (!string.IsNullOrWhiteSpace(_down.Text))
        {
            if (TryMHz(_down.Text, out double d) && d >= 0.03 && d <= 9999) down = (long)Math.Round(d * 1e6);
            else ok = false;
        }
        _down.ForeColor = ok ? _palette.Text : _palette.Danger;

        long offset = 0;
        bool offOk = true;
        if (!string.IsNullOrWhiteSpace(_offset.Text))
        {
            if (TryMHz(_offset.Text, out double o) && Math.Abs(o) <= 9999) offset = (long)Math.Round(o * 1e6);
            else offOk = false;
        }
        _offset.ForeColor = offOk ? _palette.Text : _palette.Danger;

        if (ok && offOk) FrequenciesChanged?.Invoke(down, offset);
    }

    private static bool TryMHz(string text, out double mhz)
    {
        text = text.Trim().Replace(" ", "");
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out mhz)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out mhz);
    }

    private static Button MakeButton(string text) => new()
    {
        Text = text,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        FlatStyle = FlatStyle.Flat,
        Margin = new Padding(2, 2, 2, 2),
        Padding = new Padding(4, 0, 4, 0),
        UseVisualStyleBackColor = false,
    };

    private void StyleButton(Button b, bool danger)
    {
        b.BackColor = danger ? _palette.Danger : _palette.Panel;
        b.ForeColor = danger ? Color.White : _palette.Text;
        b.FlatAppearance.BorderColor = danger ? _palette.Danger : _palette.PanelEdge;
        b.FlatAppearance.MouseOverBackColor = danger ? _palette.Danger : Blend(_palette.Panel, _palette.Rotator, 0.18);
    }

    private static void SetText(Control c, string text)
    {
        if (c.Text != text) c.Text = text;
    }

    private static Color Blend(Color a, Color b, double t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
}
