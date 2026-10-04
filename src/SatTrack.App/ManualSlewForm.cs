using SatTrack.Core.Settings;
using SatTrack.Core.Tracking;

namespace SatTrack.App;

/// <summary>
/// Small tool window for sending the rotator to a typed-in position. Stays open (non-modal)
/// so the gauges can be watched while the rotator moves.
/// </summary>
public sealed class ManualSlewForm : Form
{
    private readonly TrackingEngine _engine;
    private readonly Func<AppSettings> _settings;

    private readonly NumericUpDown _az = new() { DecimalPlaces = 0, Increment = 5, Width = 90, TextAlign = HorizontalAlignment.Right };
    private readonly NumericUpDown _el = new() { DecimalPlaces = 0, Increment = 5, Width = 90, TextAlign = HorizontalAlignment.Right };
    private readonly Label _azRange = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(6, 7, 3, 3) };
    private readonly Label _elRange = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(6, 7, 3, 3) };
    private readonly Label _position = new() { AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
    private readonly Label _state = new() { AutoSize = true, MaximumSize = new Size(330, 0), Margin = new Padding(3, 6, 3, 3) };
    private readonly Button _slew = new() { Text = "Slew", AutoSize = true, MinimumSize = new Size(80, 0) };
    private readonly Button _stop = new() { Text = "Stop", AutoSize = true, MinimumSize = new Size(80, 0) };
    private readonly Button _useCurrent = new() { Text = "Use current", AutoSize = true };
    private readonly Button _park = new() { Text = "Park", AutoSize = true };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };

    public ManualSlewForm(TrackingEngine engine, Func<AppSettings> settings)
    {
        _engine = engine;
        _settings = settings;

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Manual slew";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font("Segoe UI", 9f);
        Padding = new Padding(10);
        KeyPreview = true;

        var table = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Dock = DockStyle.Fill };
        table.Controls.Add(new Label { Text = "Azimuth (°)", AutoSize = true, Margin = new Padding(3, 7, 10, 3) }, 0, 0);
        table.Controls.Add(_az, 1, 0);
        table.Controls.Add(_azRange, 2, 0);
        table.Controls.Add(new Label { Text = "Elevation (°)", AutoSize = true, Margin = new Padding(3, 7, 10, 3) }, 0, 1);
        table.Controls.Add(_el, 1, 1);
        table.Controls.Add(_elRange, 2, 1);
        table.Controls.Add(new Label { Text = "Rotator now", AutoSize = true, Margin = new Padding(3, 8, 10, 3) }, 0, 2);
        table.Controls.Add(_position, 1, 2);
        table.SetColumnSpan(_position, 2);

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
        buttons.Controls.AddRange(new Control[] { _slew, _stop, _useCurrent, _park });

        var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
        root.Controls.Add(table);
        root.Controls.Add(buttons);
        root.Controls.Add(_state);
        Controls.Add(root);

        AcceptButton = _slew;
        ResumeLayout(false);
        PerformLayout();

        _slew.Click += (_, _) => Slew();
        _stop.Click += (_, _) => _engine.StopMotion();
        _useCurrent.Click += (_, _) => UseCurrent();
        _park.Click += (_, _) => Park();
        _timer.Tick += (_, _) => UpdateState();

        ApplyLimits();
        var snap = _engine.Snapshot;
        if (snap.Target is { } t && snap.Manual) SetFields(t.Azimuth, t.Elevation);
        else UseCurrent();
        UpdateState();
        _timer.Start();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Esc disarms (and stops) the rotator from this window too, same as the main window.
        if (keyData == Keys.Escape)
        {
            _engine.Disarm();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        base.OnFormClosed(e);
    }

    private void ApplyLimits()
    {
        var s = _settings();
        _az.Maximum = s.MaxAzimuth;
        _el.Maximum = s.MaxElevation;
        _azRange.Text = s.MaxAzimuth > 360 ? "0–450 (over 360 uses the overlap)" : "0–360";
        _elRange.Text = s.MaxElevation > 90 ? "0–180 (over 90 is flipped)" : "0–90";
    }

    private void SetFields(double az, double el)
    {
        _az.Value = Math.Clamp((decimal)Math.Round(az), _az.Minimum, _az.Maximum);
        _el.Value = Math.Clamp((decimal)Math.Round(el), _el.Minimum, _el.Maximum);
    }

    private void Slew()
    {
        ApplyLimits();
        if (!_engine.ManualGoTo((double)_az.Value, (double)_el.Value, out var error))
            _state.Text = error;
        UpdateState();
    }

    private void UseCurrent()
    {
        if (_engine.Snapshot.RotatorPosition is { } p) SetFields(p.Azimuth, p.Elevation);
    }

    private void Park()
    {
        var s = _settings();
        SetFields(s.ParkAzimuth, s.ParkElevation);
        Slew();
    }

    private void UpdateState()
    {
        if (_az.Maximum != _settings().MaxAzimuth || _el.Maximum != _settings().MaxElevation) ApplyLimits();

        var snap = _engine.Snapshot;
        bool connected = snap.Connection == RotatorConnection.Connected;

        _position.Text = snap.RotatorPosition is { } p ? $"az {p.Azimuth:0}°, el {p.Elevation:0}°" : "—";
        _slew.Enabled = connected && snap.Armed;
        _park.Enabled = connected && snap.Armed;
        _stop.Enabled = connected;
        _useCurrent.Enabled = snap.RotatorPosition is not null;

        string state = !connected ? "Not connected. Use Connect in the main window."
                     : !snap.Armed ? "Press Enable in the main window to allow movement."
                     : snap.Tracking ? "Tracking is on. Slew will stop tracking."
                     : snap.Manual ? snap.Activity
                     : "Ready. Enter a position and press Slew (or Enter). Esc disarms.";
        if (_state.Text != state) _state.Text = state;
    }
}
