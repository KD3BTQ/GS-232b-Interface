using System.IO.Ports;
using SatTrack.Core.Geo;
using SatTrack.Core.Settings;

namespace SatTrack.App;

/// <summary>Location, rotator and tracking settings.</summary>
public sealed class SettingsForm : Form
{
    private readonly AppSettings _settings;

    private readonly TextBox _location = new() { Width = 220 };
    private readonly NumericUpDown _altitude = Num(-500, 9000, 0, 1);
    private readonly Label _parsed = new() { AutoSize = true, MaximumSize = new Size(360, 0) };

    private readonly ComboBox _port = new() { Width = 120, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly ComboBox _baud = new() { Width = 120, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _azRange = new() { Width = 220, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _elRange = new() { Width = 220, DropDownStyle = ComboBoxStyle.DropDownList };

    private readonly NumericUpDown _threshold = Num(0.5m, 20, 1, 0.5m);
    private readonly NumericUpDown _lead = Num(0, 10, 1, 0.5m);
    private readonly NumericUpDown _prePosition = Num(0, 30, 1, 0.5m);
    private readonly CheckBox _park = new() { Text = "Park the rotator after each pass", AutoSize = true };
    private readonly NumericUpDown _parkAz = Num(0, 450, 0, 1);
    private readonly NumericUpDown _parkEl = Num(0, 180, 0, 1);

    public AppSettings Result => _settings;

    public SettingsForm(AppSettings current, string? notice = null)
    {
        _settings = current.Clone();

        Text = "Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font("Segoe UI", 9f);
        Padding = new Padding(12);

        SuspendLayout();

        var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };

        if (notice is not null)
            root.Controls.Add(new Label { Text = notice, AutoSize = true, MaximumSize = new Size(440, 0), Margin = new Padding(3, 0, 3, 10) });

        // ---- Location
        var loc = Section("Your location");
        AddRow(loc, "Grid square or lat, lon", _location);
        AddRow(loc, "Altitude (m)", _altitude);
        loc.Controls.Add(new Label(), 0, loc.RowCount);
        loc.Controls.Add(_parsed, 1, loc.RowCount);
        loc.RowCount++;
        root.Controls.Add(loc.Parent!);

        // ---- Rotator
        var rot = Section("Rotator (Yaesu GS-232B)");
        AddRow(rot, "COM port", _port);
        AddRow(rot, "Baud rate", _baud);
        AddRow(rot, "Azimuth travel", _azRange);
        AddRow(rot, "Elevation travel", _elRange);
        root.Controls.Add(rot.Parent!);

        // ---- Tracking
        var trk = Section("Tracking");
        AddRow(trk, "Move when target changes by (°)", _threshold);
        AddRow(trk, "Aim ahead of the satellite (s)", _lead);
        AddRow(trk, "Move to the rise point early (min)", _prePosition);
        trk.Controls.Add(_park, 0, trk.RowCount);
        trk.SetColumnSpan(_park, 2);
        trk.RowCount++;
        AddRow(trk, "Park azimuth (°)", _parkAz);
        AddRow(trk, "Park elevation (°)", _parkEl);
        root.Controls.Add(trk.Parent!);

        // ---- Buttons
        var save = new Button { Text = "Save", DialogResult = DialogResult.None, AutoSize = true, MinimumSize = new Size(80, 0) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(80, 0) };
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill,
            Margin = new Padding(0, 10, 0, 0),
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        root.Controls.Add(buttons);

        Controls.Add(root);
        AcceptButton = save;
        CancelButton = cancel;

        ResumeLayout(false);
        PerformLayout();

        // ---- Values
        _location.Text = _settings.LocationText;
        _altitude.Value = (decimal)Math.Clamp(_settings.AltitudeMeters, -500, 9000);

        foreach (var p in SafePortNames()) _port.Items.Add(p);
        _port.Text = _settings.ComPort;

        foreach (int b in new[] { 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200 }) _baud.Items.Add(b);
        _baud.SelectedItem = _settings.BaudRate;
        if (_baud.SelectedIndex < 0) _baud.SelectedItem = 9600;

        _azRange.Items.AddRange(new object[] { "0–360°", "0–450° (90° overlap)" });
        _azRange.SelectedIndex = _settings.MaxAzimuth > 360 ? 1 : 0;
        _elRange.Items.AddRange(new object[] { "0–90°", "0–180° (allows flip over the top)" });
        _elRange.SelectedIndex = _settings.MaxElevation > 90 ? 1 : 0;

        _threshold.Value = Clamp(_settings.CommandThresholdDeg, _threshold);
        _lead.Value = Clamp(_settings.LeadSeconds, _lead);
        _prePosition.Value = Clamp(_settings.PrePositionMinutes, _prePosition);
        _park.Checked = _settings.ParkAfterPass;
        _parkAz.Value = Clamp(_settings.ParkAzimuth, _parkAz);
        _parkEl.Value = Clamp(_settings.ParkElevation, _parkEl);

        _location.TextChanged += (_, _) => UpdateParsed();
        _park.CheckedChanged += (_, _) => _parkAz.Enabled = _parkEl.Enabled = _park.Checked;
        _parkAz.Enabled = _parkEl.Enabled = _park.Checked;
        UpdateParsed();

        save.Click += (_, _) => OnSave();
    }

    private void UpdateParsed()
    {
        if (StationLocation.TryParse(_location.Text, (double)_altitude.Value, out var loc, out var error))
        {
            _parsed.ForeColor = SystemColors.GrayText;
            _parsed.Text = loc!.ToString();
        }
        else
        {
            _parsed.ForeColor = Color.Firebrick;
            _parsed.Text = string.IsNullOrWhiteSpace(_location.Text) ? "Examples: FM19la or 39.29, -76.61" : error;
        }
    }

    private void OnSave()
    {
        if (!StationLocation.TryParse(_location.Text, (double)_altitude.Value, out _, out var error))
        {
            MessageBox.Show(this, error, "Location", MessageBoxButtons.OK, MessageBoxIcon.Information);
            _location.Focus();
            return;
        }
        if (string.IsNullOrWhiteSpace(_port.Text))
        {
            MessageBox.Show(this, "Enter the COM port the GS-232B is on, for example COM3.", "COM port",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
            _port.Focus();
            return;
        }

        _settings.LocationText = _location.Text.Trim();
        _settings.AltitudeMeters = (double)_altitude.Value;
        _settings.ComPort = _port.Text.Trim().ToUpperInvariant();
        _settings.BaudRate = (int)(_baud.SelectedItem ?? 9600);
        _settings.MaxAzimuth = _azRange.SelectedIndex == 1 ? 450 : 360;
        _settings.MaxElevation = _elRange.SelectedIndex == 1 ? 180 : 90;
        _settings.CommandThresholdDeg = (double)_threshold.Value;
        _settings.LeadSeconds = (double)_lead.Value;
        _settings.PrePositionMinutes = (double)_prePosition.Value;
        _settings.ParkAfterPass = _park.Checked;
        _settings.ParkAzimuth = Math.Min((double)_parkAz.Value, _settings.MaxAzimuth);
        _settings.ParkElevation = Math.Min((double)_parkEl.Value, _settings.MaxElevation);

        DialogResult = DialogResult.OK;
        Close();
    }

    // ---------------------------------------------------------------- layout helpers

    private static TableLayoutPanel Section(string title)
    {
        var group = new GroupBox
        {
            Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill, Padding = new Padding(8), Margin = new Padding(0, 0, 0, 8),
        };
        var table = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        group.Controls.Add(table);
        return table;
    }

    private static void AddRow(TableLayoutPanel table, string label, Control control)
    {
        var l = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 12, 3) };
        control.Anchor = AnchorStyles.Left;
        table.Controls.Add(l, 0, table.RowCount);
        table.Controls.Add(control, 1, table.RowCount);
        table.RowCount++;
    }

    private static NumericUpDown Num(decimal min, decimal max, int decimals, decimal step) => new()
    {
        Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = step, Width = 80,
        TextAlign = HorizontalAlignment.Right,
    };

    private static decimal Clamp(double v, NumericUpDown n) =>
        Math.Clamp((decimal)v, n.Minimum, n.Maximum);

    private static string[] SafePortNames()
    {
        try
        {
            return SerialPort.GetPortNames()
                             .Distinct()
                             .OrderBy(p => p.Length).ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
                             .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
