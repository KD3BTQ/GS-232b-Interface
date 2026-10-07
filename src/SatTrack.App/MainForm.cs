using System.Runtime.InteropServices;
using SatTrack.App.Controls;
using SatTrack.Core.Catalog;
using SatTrack.Core.Orbit;
using SatTrack.Core.Radio;
using SatTrack.Core.Rotator;
using SatTrack.Core.Settings;
using SatTrack.Core.Time;
using SatTrack.Core.Tracking;

namespace SatTrack.App;

public sealed class MainForm : Form
{
    // ---------------------------------------------------------------- state
    private AppSettings _settings;
    private readonly OrbitalElementsService _elements;
    private readonly TrackingEngine _engine;
    private SatelliteCatalog _catalog = SatelliteCatalog.LoadBuiltIn();
    private SimulationClock? _simClock;
    private IRotator? _rotator;
    private readonly CommLog _commLog = new();
    private DebugForm? _debugForm;
    private readonly RadioController _radio;
    private readonly CommLog _radioLog = new();
    private DebugForm? _radioDebugForm;
    private bool _radioConnecting;
    private string _seriesKey = "";
    private int _seriesTick;
    private ManualSlewForm? _slewForm;
    private PassesForm? _passesForm;
    private Palette _palette = Palette.Dark;
    private bool _simulating;
    private bool _connecting;
    private bool _refreshing;
    private bool _populatingCombo;
    private int _tick;

    // ---------------------------------------------------------------- controls
    private readonly ToolStrip _tool = new() { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(4, 2, 4, 2) };
    private readonly ToolStripComboBox _satCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = 180, MaxDropDownItems = 24 };
    private readonly ToolStripButton _btnConnect = new("Connect") { ToolTipText = "Connect to the rotator and read its position" };
    private readonly ToolStripButton _btnEnable = new("Enable") { ToolTipText = "Allow the app to move the rotator (Esc disarms at any time)" };
    private readonly ToolStripButton _btnTrack = new("Track") { ToolTipText = "Follow the selected satellite" };
    private readonly ToolStripButton _btnPasses = new("Upcoming passes") { ToolTipText = "Every pass of every satellite in the list (Ctrl+P)" };
    private readonly ToolStripLabel _lblSim = new("Simulation") { ToolTipText = "Simulation mode is on (Menu > Simulation mode to turn it off)" };
    private readonly ToolStripButton _btnNextPass = new("Next pass") { ToolTipText = "Jump the simulation clock to just before the next pass" };
    private readonly ToolStripComboBox _speedCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = 64, ToolTipText = "Simulation speed" };
    private readonly ToolStripDropDownButton _menu = new("Menu") { Alignment = ToolStripItemAlignment.Right };
    private readonly ToolStripButton _btnOnTop = new("On top") { Alignment = ToolStripItemAlignment.Right, CheckOnClick = true, ToolTipText = "Keep this window above other windows" };

    private readonly ToolStripMenuItem _miMercator = new("Mercator map");
    private readonly ToolStripMenuItem _miPlanar = new("Planar map (centred on you)");
    private readonly ToolStripMenuItem _miDark = new("Dark mode");
    private readonly ToolStripMenuItem _miOnTop = new("Always on top");
    private readonly ToolStripMenuItem _miSimulate = new("Simulation mode") { ToolTipText = "Use a simulated rotator and clock to see how tracking looks" };

    private readonly StatusStrip _status = new() { SizingGrip = true };
    private readonly ToolStripStatusLabel _lblConn = new();
    private readonly ToolStripStatusLabel _lblArm = new();
    private readonly ToolStripStatusLabel _lblData = new();
    private readonly ToolStripStatusLabel _lblStation = new() { IsLink = true, ToolTipText = "Change location" };
    private readonly ToolStripStatusLabel _lblMsg = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _lblClock = new();

    private readonly MapView _map = new() { Dock = DockStyle.Fill, Margin = new Padding(0) };
    private readonly FrequencyPanel _freq = new() { Dock = DockStyle.Fill, Margin = new Padding(3, 0, 0, 0) };
    private readonly TableLayoutPanel _top = new() { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
    private readonly ToolStripMenuItem _miFreqPanel = new("Show frequency panel");
    private readonly ToolStripStatusLabel _lblRadio = new();
    private readonly AzimuthGauge _azGauge = new() { Dock = DockStyle.Fill, Margin = new Padding(0, 3, 2, 0) };
    private readonly ElevationGauge _elGauge = new() { Dock = DockStyle.Fill, Margin = new Padding(2, 3, 0, 0) };

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 100 };

    public MainForm()
    {
        _settings = SettingsStore.Load();
        _elements = new OrbitalElementsService(SettingsStore.ElementCacheFolder);
        _engine = new TrackingEngine(_elements);
        _radio = new RadioController(() => _engine.Snapshot);

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9f);
        Text = AppInfo.Name;
        MinimumSize = new Size(560, 360);
        Size = new Size(1100, 720);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        BuildToolbar();
        BuildStatusBar();

        var gauges = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
        gauges.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        gauges.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        gauges.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        gauges.Controls.Add(_azGauge, 0, 0);
        gauges.Controls.Add(_elGauge, 1, 0);

        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0), Padding = new Padding(3) };
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        _top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _top.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _top.Controls.Add(_map, 0, 0);
        _top.Controls.Add(_freq, 1, 0);
        content.Controls.Add(_top, 0, 0);
        content.Controls.Add(gauges, 0, 1);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _tool.Dock = DockStyle.Fill;
        _status.Dock = DockStyle.Fill;
        root.Controls.Add(_tool, 0, 0);
        root.Controls.Add(content, 0, 1);
        root.Controls.Add(_status, 0, 2);
        Controls.Add(root);

        ResumeLayout(false);
        PerformLayout();

        RestoreWindowBounds();

        _radio.Message += msg =>
        {
            _radioLog.Info(msg);
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke(new Action(() => ShowMessage(msg)));
        };
        _freq.ConnectClicked += async (_, _) => await OnRadioConnect();
        _freq.EnableClicked += (_, _) => OnRadioEnable();
        _freq.TrackClicked += (_, _) => OnRadioTrack();
        _freq.FrequenciesChanged += OnFrequenciesChanged;

        _engine.Message += msg =>
        {
            _commLog.Info(msg);
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke(new Action(() => ShowMessage(msg)));
        };
        _map.ProjectionChosen += SetProjection;
        _map.DarkModeChosen += SetDarkMode;
        _timer.Tick += (_, _) => OnTick();
    }

    // ---------------------------------------------------------------- construction

    private void BuildToolbar()
    {
        _satCombo.ComboBox.FormattingEnabled = true;
        _satCombo.ComboBox.Format += (_, e) =>
        {
            if (e.ListItem is SatelliteEntry s)
                e.Value = s.HasInlineElements || _elements.Get(s.NoradId) is not null ? s.Name : $"{s.Name}  (no data)";
        };
        _satCombo.SelectedIndexChanged += (_, _) => OnSatelliteChanged();

        _speedCombo.Items.AddRange(new object[] { "1×", "5×", "20×", "60×" });
        _speedCombo.SelectedIndexChanged += (_, _) => OnSpeedChanged();

        _btnConnect.Click += async (_, _) => await OnConnectClick();
        _btnEnable.Click += (_, _) => OnEnableClick();
        _btnTrack.Click += (_, _) => OnTrackClick();
        _btnPasses.Click += (_, _) => ShowPassesWindow();
        _miSimulate.Click += async (_, _) => await ToggleSimulation();
        _btnNextPass.Click += (_, _) => JumpToNextPass();
        _btnOnTop.CheckedChanged += (_, _) => SetAlwaysOnTop(_btnOnTop.Checked);

        _btnEnable.Tag = "keepcolor";

        var miSettings = new ToolStripMenuItem("Settings…", null, (_, _) => OpenSettings(null));
        var miUpdate = new ToolStripMenuItem("Update orbital data", null, async (_, _) => await RefreshElementsAsync(force: true)) { ShortcutKeys = Keys.F5 };
        var miLoad = new ToolStripMenuItem("Load satellite list…", null, (_, _) => LoadListFromFile());
        var miBuiltIn = new ToolStripMenuItem("Use built-in satellite list", null, (_, _) => LoadCatalog(null, showErrors: true));
        var miExport = new ToolStripMenuItem("Save built-in list as…", null, (_, _) => ExportBuiltIn());
        var miPasses = new ToolStripMenuItem("Upcoming passes…", null, (_, _) => ShowPassesWindow()) { ShortcutKeys = Keys.Control | Keys.P };
        var miSlew = new ToolStripMenuItem("Manual slew…", null, (_, _) => ShowSlewWindow()) { ShortcutKeys = Keys.Control | Keys.M };
        var miDebug = new ToolStripMenuItem("Rotator serial debug…", null, (_, _) => ShowDebugWindow()) { ShortcutKeys = Keys.Control | Keys.D };
        var miRadioDebug = new ToolStripMenuItem("Radio serial debug…", null, (_, _) => ShowRadioDebugWindow()) { ShortcutKeys = Keys.Control | Keys.R };
        _miFreqPanel.Click += (_, _) => { _settings.ShowFrequencyPanel = !_settings.ShowFrequencyPanel; ApplyFrequencyPanelLayout(); };
        _miMercator.Click += (_, _) => SetProjection(MapProjection.Mercator);
        _miPlanar.Click += (_, _) => SetProjection(MapProjection.Planar);
        _miDark.Click += (_, _) => SetDarkMode(!_settings.DarkMode);
        _miOnTop.Click += (_, _) => SetAlwaysOnTop(!_settings.AlwaysOnTop);

        _menu.DropDownItems.AddRange(new ToolStripItem[]
        {
            miSettings, miUpdate, new ToolStripSeparator(),
            miPasses, miSlew, _miSimulate, miDebug, miRadioDebug, new ToolStripSeparator(),
            miLoad, miBuiltIn, miExport, new ToolStripSeparator(),
            _miFreqPanel, _miMercator, _miPlanar, _miDark, new ToolStripSeparator(),
            _miOnTop,
        });

        _tool.Items.AddRange(new ToolStripItem[]
        {
            _satCombo, new ToolStripSeparator(),
            _btnConnect, _btnEnable, _btnTrack, new ToolStripSeparator(),
            _btnPasses, new ToolStripSeparator(),
            _lblSim, _btnNextPass, _speedCombo,
            _menu, _btnOnTop,
        });
    }

    private void BuildStatusBar()
    {
        _lblStation.Click += (_, _) => OpenSettings(null);
        _status.Items.AddRange(new ToolStripItem[] { _lblConn, _lblArm, _lblRadio, _lblStation, _lblData, _lblMsg, _lblClock });
        foreach (ToolStripItem item in _status.Items) item.Margin = new Padding(6, 3, 6, 2);
    }

    // ---------------------------------------------------------------- lifecycle

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        ApplyTheme();
        _map.Projection = _settings.Projection;
        SetAlwaysOnTop(_settings.AlwaysOnTop);
        _speedCombo.SelectedIndex = SpeedIndex(_settings.SimulationSpeed);

        _elements.LoadCache();
        ApplyEngineConfig();
        ApplyRadioConfig();
        ApplyFrequencyPanelLayout();
        LoadCatalog(_settings.CatalogPath, showErrors: false);

        _engine.Start();
        _radio.Start();
        _timer.Start();
        UpdateSimulationUi();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);

        if (_settings.GetStation() is null)
            OpenSettings($"Welcome to the {AppInfo.Name}. Enter where your rotator is (a grid square like FM19la, or latitude, longitude) and the COM port of the GS-232B.");

        if (_elements.IsStale)
            await RefreshElementsAsync(force: false);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _timer.Stop();
        SaveWindowBounds();
        TrySaveSettings();
        _radio.Dispose();  // stops sending frequency commands
        _engine.Dispose(); // disarms and stops the rotator
        base.OnFormClosing(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            _engine.Disarm();
            _radio.Disarm();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ---------------------------------------------------------------- periodic UI update

    private void OnTick()
    {
        var snap = _engine.Snapshot;
        _map.Snapshot = snap;
        _azGauge.Snapshot = snap;
        _elGauge.Snapshot = snap;

        // Toolbar state.
        bool connected = snap.Connection == RotatorConnection.Connected;
        SetText(_btnConnect, _connecting ? "Connecting…"
                           : snap.Connection == RotatorConnection.Connected ? "Disconnect"
                           : snap.Connection == RotatorConnection.Lost ? "Reconnect"
                           : "Connect");
        _btnConnect.Enabled = !_connecting;

        _btnEnable.Enabled = connected || snap.Armed;
        SetText(_btnEnable, snap.Armed ? "Disarm" : "Enable");
        var enableBack = snap.Armed ? _palette.Danger : Color.Empty;
        if (_btnEnable.BackColor != enableBack)
        {
            _btnEnable.BackColor = enableBack;
            _btnEnable.ForeColor = snap.Armed ? Color.White : _palette.Text;
            _btnEnable.Tag = snap.Armed ? "keepcolor" : null;
        }

        _btnTrack.Enabled = snap.SatelliteName is not null;
        SetText(_btnTrack, snap.Tracking ? "Stop tracking" : "Track");
        _btnTrack.Checked = snap.Tracking;

        // Status bar.
        switch (snap.Connection)
        {
            case RotatorConnection.Connected:
                SetStatus(_lblConn, snap.RotatorDescription ?? "Connected", _palette.Good);
                break;
            case RotatorConnection.Lost:
                SetStatus(_lblConn, "Rotator connection lost", _palette.Danger);
                break;
            case RotatorConnection.Connecting:
                SetStatus(_lblConn, "Connecting…", _palette.TextDim);
                break;
            default:
                SetStatus(_lblConn, _connecting ? "Connecting…" : "Rotator not connected", _palette.TextDim);
                break;
        }

        SetStatus(_lblArm, snap.Armed ? "Enabled: rotator will move (Esc to disarm)" : "Disarmed",
                  snap.Armed ? _palette.Danger : _palette.TextDim);

        SetStatus(_lblStation, snap.Station?.GridSquare ?? "Set location", snap.Station is null ? _palette.Danger : _palette.Text);

        if (_refreshing)
        {
            SetStatus(_lblData, "Updating orbital data…", _palette.TextDim);
        }
        else if (snap.Elements is { } el)
        {
            var age = snap.UtcNow - el.EpochUtc;
            SetStatus(_lblData, $"Elements {FormatAge(age)} old ({el.Source})", age.TotalDays > 14 ? _palette.Danger : _palette.TextDim);
        }
        else
        {
            SetStatus(_lblData, snap.SatelliteName is null ? "" : "No orbital data", _palette.TextDim);
        }

        string clock = $"{snap.UtcNow:HH:mm:ss}Z";
        SetStatus(_lblClock, snap.Simulating ? $"Simulated {clock}  {snap.ClockRate:0}×" : clock,
                  snap.Simulating ? _palette.Target : _palette.Text);

        // Radio and frequency panel.
        var rs = _radio.Snapshot;
        if (_freq.Visible)
        {
            _freq.UpdateState(rs, snap, _radioConnecting);
            string key = $"{snap.SatelliteName}|{snap.Pass?.AosUtc:O}|{rs.NominalDownlinkHz}";
            if (key != _seriesKey || ++_seriesTick % 20 == 0) RefreshDopplerSeries(snap, key);
        }
        switch (rs.Connection)
        {
            case RotatorConnection.Connected:
                SetStatus(_lblRadio, rs.Armed ? "Radio enabled" : "Radio connected", rs.Armed ? _palette.Danger : _palette.Good);
                break;
            case RotatorConnection.Lost:
                SetStatus(_lblRadio, "Radio connection lost", _palette.Danger);
                break;
            default:
                SetStatus(_lblRadio, _radioConnecting ? "Radio connecting…" : _freq.Visible ? "Radio not connected" : "", _palette.TextDim);
                break;
        }
        _lblRadio.ToolTipText = rs.Description ?? "";

        // Every minute: refresh orbital data when it has gone stale.
        if (++_tick % 600 == 0 && _elements.IsStale && !_refreshing)
            _ = RefreshElementsAsync(force: false);
    }

    private static void SetText(ToolStripItem item, string text)
    {
        if (item.Text != text) item.Text = text;
    }

    private static void SetStatus(ToolStripStatusLabel label, string text, Color color)
    {
        if (label.Text != text) label.Text = text;
        if (label.ForeColor != color)
        {
            label.ForeColor = color;
            if (label.IsLink) label.LinkColor = label.ActiveLinkColor = label.VisitedLinkColor = color;
        }
    }

    private void ShowMessage(string text)
    {
        _lblMsg.Text = text;
        _lblMsg.ToolTipText = text;
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalHours < 1) return "under 1 h";
        if (age.TotalHours < 48) return $"{(int)age.TotalHours} h";
        return $"{(int)age.TotalDays} days";
    }

    // ---------------------------------------------------------------- rotator buttons

    private async Task OnConnectClick()
    {
        var conn = _engine.Snapshot.Connection;
        if (conn == RotatorConnection.Connected)
        {
            await _engine.DisconnectAsync();
            _rotator = null;
            return;
        }

        IRotator rotator = _simulating
            ? new SimulatedRotator(_engine.Clock, startAzimuth: 180, startElevation: 0)
            {
                MaxAzimuth = _settings.MaxAzimuth,
                MaxElevation = _settings.MaxElevation,
                Log = _commLog,
            }
            : new Gs232bRotator(_settings.ComPort, _settings.BaudRate)
            {
                MaxAzimuth = _settings.MaxAzimuth,
                MaxElevation = _settings.MaxElevation,
                Log = _commLog,
            };

        _connecting = true;
        try
        {
            await _engine.ConnectAsync(rotator);
            _rotator = rotator;
        }
        catch (Exception ex)
        {
            _rotator = null;
            ShowMessage("Connection failed.");
            MessageBox.Show(this, ex.Message, "Couldn't connect to the rotator", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _connecting = false;
        }
    }

    private void OnEnableClick()
    {
        if (_engine.IsArmed)
        {
            _engine.Disarm();
            return;
        }
        if (!_engine.Arm())
            ShowMessage("Connect to the rotator before enabling it.");
    }

    private void OnTrackClick()
    {
        if (_engine.IsTracking) _engine.StopTracking();
        else _engine.StartTracking();
    }

    // ---------------------------------------------------------------- simulation

    private async Task ToggleSimulation()
    {
        bool on = !_simulating;

        if (_engine.Snapshot.Connection != RotatorConnection.Disconnected)
        {
            await _engine.DisconnectAsync();
            _rotator = null;
        }
        if (_radio.Snapshot.Connection != RotatorConnection.Disconnected)
            await _radio.DisconnectAsync();

        _simulating = on;
        if (on)
        {
            _simClock = new SimulationClock(DateTime.UtcNow) { Rate = _settings.SimulationSpeed };
            _engine.SetClock(_simClock);
            ShowMessage("Simulation on. Connect uses a simulated rotator; Next pass skips ahead in time.");
            _commLog.Info("Simulation mode on");
            _radioLog.Info("Simulation mode on");
        }
        else
        {
            _engine.SetClock(SystemClock.Instance);
            _simClock = null;
            ShowMessage("Simulation off. Back to real time.");
            _commLog.Info("Simulation mode off");
            _radioLog.Info("Simulation mode off");
        }
        UpdateSimulationUi();
    }

    private void UpdateSimulationUi()
    {
        _miSimulate.Checked = _simulating;
        _lblSim.Visible = _simulating;
        _btnNextPass.Visible = _simulating;
        _speedCombo.Visible = _simulating;
        Text = _simulating ? $"{AppInfo.Name} (simulation)" : AppInfo.Name;
    }

    private void JumpToNextPass()
    {
        if (_simClock is null) return;
        var now = _simClock.UtcNow;
        var pass = _engine.PredictPass(now, skipCurrent: true);
        if (pass is null)
        {
            ShowMessage("No pass found in the next two days.");
            return;
        }
        if (pass.NeverSets)
        {
            ShowMessage("This satellite never sets from your location; there's no next pass to jump to.");
            return;
        }
        var lead = TimeSpan.FromMinutes(Math.Max(1.5, _settings.PrePositionMinutes + 0.5));
        _simClock.JumpTo(pass.AosUtc - lead);
        ShowMessage($"Jumped to {lead.TotalMinutes:0.#} min before the {pass.AosUtc:HH:mm}Z pass (max {pass.MaxElevation:0}°).");
    }

    private void OnSpeedChanged()
    {
        double rate = _speedCombo.SelectedIndex switch { 1 => 5, 2 => 20, 3 => 60, _ => 1 };
        _settings.SimulationSpeed = rate;
        if (_simClock is not null) _simClock.Rate = rate;
    }

    private static int SpeedIndex(double rate) => rate switch { >= 60 => 3, >= 20 => 2, >= 5 => 1, _ => 0 };

    // ---------------------------------------------------------------- satellites

    private void LoadCatalog(string? path, bool showErrors)
    {
        SatelliteCatalog catalog;
        try
        {
            catalog = path is null ? SatelliteCatalog.LoadBuiltIn() : SatelliteCatalog.LoadFromFile(path);
        }
        catch (Exception ex)
        {
            if (showErrors)
                MessageBox.Show(this, ex.Message, "Couldn't load the satellite list", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else
                ShowMessage($"Couldn't load {path}; using the built-in list.");
            catalog = SatelliteCatalog.LoadBuiltIn();
            path = null;
        }

        _catalog = catalog;
        _settings.CatalogPath = path;

        _populatingCombo = true;
        _satCombo.Items.Clear();
        foreach (var s in catalog.Satellites) _satCombo.Items.Add(s);

        var previous = catalog.Satellites.FirstOrDefault(s => s.NoradId > 0 && s.NoradId == _settings.SelectedNoradId)
                    ?? catalog.Satellites.FirstOrDefault(s => s.Name == _settings.SelectedName)
                    ?? catalog.Satellites[0];
        _satCombo.SelectedItem = previous;
        _populatingCombo = false;
        OnSatelliteChanged();

        string source = catalog.IsBuiltIn ? "built-in list" : Path.GetFileName(catalog.Source);
        string msg = $"{catalog.Satellites.Count} satellites from the {source}.";
        if (catalog.Warnings.Count > 0) msg += $" {catalog.Warnings.Count} entries skipped: {catalog.Warnings[0]}";
        ShowMessage(msg);

        bool missing = catalog.Satellites.Any(s => !s.HasInlineElements && _elements.Get(s.NoradId) is null);
        if (missing && IsHandleCreated && !_refreshing && !_elements.IsStale)
            _ = RefreshElementsAsync(force: true);
    }

    private void OnSatelliteChanged()
    {
        if (_populatingCombo) return;
        var entry = _satCombo.SelectedItem as SatelliteEntry;
        _engine.SelectSatellite(entry);
        _settings.SelectedNoradId = entry?.NoradId;
        _settings.SelectedName = entry?.Name;
        _satCombo.ToolTipText = entry?.Notes ?? entry?.Name ?? "";

        if (_radio.IsTracking) _radio.StopTracking();
        LoadFrequenciesFor(entry);
    }

    private void LoadListFromFile()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Load satellite list",
            Filter = "Satellite list (*.json)|*.json|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            LoadCatalog(dlg.FileName, showErrors: true);
            TrySaveSettings();
        }
    }

    private void ExportBuiltIn()
    {
        using var dlg = new SaveFileDialog
        {
            Title = "Save built-in satellite list",
            Filter = "Satellite list (*.json)|*.json",
            FileName = "satellites.json",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            File.WriteAllText(dlg.FileName, SatelliteCatalog.GetBuiltInJson());
            ShowMessage($"Saved {Path.GetFileName(dlg.FileName)}. Edit it, then use Menu > Load satellite list.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Couldn't save the list", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task RefreshElementsAsync(bool force)
    {
        if (_refreshing) return;
        if (!force && !_elements.IsStale) return;

        _refreshing = true;
        try
        {
            string summary = await _elements.RefreshAsync(_catalog.Satellites);
            _engine.ReloadElements();
            ShowMessage(summary);
        }
        catch (Exception ex)
        {
            ShowMessage($"Orbital data update failed: {ex.Message}");
        }
        finally
        {
            _refreshing = false;
            _satCombo.ComboBox.Invalidate();
        }
    }

    // ---------------------------------------------------------------- debug window

    private void ShowDebugWindow()
    {
        if (_debugForm is { IsDisposed: false })
        {
            if (_debugForm.WindowState == FormWindowState.Minimized) _debugForm.WindowState = FormWindowState.Normal;
            _debugForm.Activate();
            return;
        }
        _debugForm = new DebugForm(_commLog, "rotator", "GS232B-serial") { Owner = this };
        _debugForm.FormClosed += (_, _) => _debugForm = null;
        _debugForm.Show(this);
    }

    private void ShowRadioDebugWindow()
    {
        if (_radioDebugForm is { IsDisposed: false })
        {
            if (_radioDebugForm.WindowState == FormWindowState.Minimized) _radioDebugForm.WindowState = FormWindowState.Normal;
            _radioDebugForm.Activate();
            return;
        }
        _radioDebugForm = new DebugForm(_radioLog, "radio", "radio-serial") { Owner = this };
        _radioDebugForm.FormClosed += (_, _) => _radioDebugForm = null;
        _radioDebugForm.Show(this);
    }

    // ---------------------------------------------------------------- radio

    private async Task OnRadioConnect()
    {
        if (_radio.Snapshot.Connection == RotatorConnection.Connected)
        {
            await _radio.DisconnectAsync();
            return;
        }

        byte addr = (byte)Math.Clamp(_settings.RadioCivAddress, 1, 0xDF);
        IRadio radio = _simulating
            ? new SimulatedRadio(_radio.Snapshot.NominalDownlinkHz ?? 14_074_000) { RadioAddress = addr, Log = _radioLog, Protocol = _settings.RadioType }
            : _settings.RadioType == RadioType.FlexCat
                ? new FlexCatRadio(_settings.RadioComPort, _settings.RadioBaudRate) { Log = _radioLog }
                : new IcomCivRadio(_settings.RadioComPort, _settings.RadioBaudRate) { RadioAddress = addr, Log = _radioLog };

        _radioConnecting = true;
        try
        {
            await _radio.ConnectAsync(radio);
        }
        catch (Exception ex)
        {
            ShowMessage("Radio connection failed.");
            MessageBox.Show(this, ex.Message, "Couldn't connect to the radio", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _radioConnecting = false;
        }
    }

    private void OnRadioEnable()
    {
        if (_radio.IsArmed) { _radio.Disarm(); return; }
        if (!_radio.Arm()) ShowMessage("Connect to the radio before enabling it.");
    }

    private void OnRadioTrack()
    {
        if (_radio.IsTracking) _radio.StopTracking();
        else _radio.StartTracking();
    }

    private void LoadFrequenciesFor(SatelliteEntry? entry)
    {
        long? down = null;
        long offset = 0;
        if (entry is not null)
        {
            if (_settings.Frequencies.TryGetValue(AppSettings.FrequencyKey(entry), out var f))
            {
                down = f.DownlinkHz;
                offset = f.OffsetHz;
            }
            else if (entry.DownlinkMHz is double mhz)
            {
                down = (long)Math.Round(mhz * 1e6);
                offset = (long)Math.Round((entry.OffsetMHz ?? 0) * 1e6);
            }
        }
        _freq.SetFrequencies(down, offset);
        _radio.SetFrequencies(down, offset);
        _seriesKey = "";
    }

    private void OnFrequenciesChanged(long? downlinkHz, long offsetHz)
    {
        if (_satCombo.SelectedItem is SatelliteEntry entry)
            _settings.Frequencies[AppSettings.FrequencyKey(entry)] = new SatFrequency { DownlinkHz = downlinkHz, OffsetHz = offsetHz };
        _radio.SetFrequencies(downlinkHz, offsetHz);
        _seriesKey = "";
    }

    private void RefreshDopplerSeries(TrackerSnapshot snap, string key)
    {
        _seriesKey = key;
        DateTime from, to;
        if (snap.Pass is { NeverSets: false } p)
        {
            from = p.AosUtc - TimeSpan.FromMinutes(2);
            to = p.LosUtc + TimeSpan.FromMinutes(2);
        }
        else
        {
            from = snap.UtcNow - TimeSpan.FromMinutes(30);
            to = snap.UtcNow + TimeSpan.FromMinutes(30);
        }
        _freq.Doppler.Series = _engine.RangeRateSeries(from, to, 200);
    }

    private void ApplyRadioConfig() => _radio.SetOptions(_settings.RadioStepHz, _settings.RadioFollows);

    private void ApplyFrequencyPanelLayout()
    {
        bool show = _settings.ShowFrequencyPanel;
        _top.SuspendLayout();
        _top.ColumnStyles[0] = new ColumnStyle(SizeType.Percent, show ? 50 : 100);
        _top.ColumnStyles[1] = new ColumnStyle(SizeType.Percent, show ? 50 : 0);
        _freq.Visible = show;
        _top.ResumeLayout(true);
        _miFreqPanel.Checked = show;
        _seriesKey = "";
    }

    private void ShowPassesWindow()
    {
        if (_passesForm is { IsDisposed: false })
        {
            if (_passesForm.WindowState == FormWindowState.Minimized) _passesForm.WindowState = FormWindowState.Normal;
            _passesForm.Activate();
            return;
        }
        _passesForm = new PassesForm(_elements, () => _catalog, () => _settings, () => _engine.Clock.UtcNow,
                                     _palette, _settings.Projection) { Owner = this };
        _passesForm.SatelliteChosen += entry =>
        {
            _satCombo.SelectedItem = entry;
            ShowMessage($"Selected {entry.Name}.");
        };
        _passesForm.FormClosed += (_, _) => _passesForm = null;
        _passesForm.Show(this);
    }

    private void ShowSlewWindow()
    {
        if (_slewForm is { IsDisposed: false })
        {
            _slewForm.Activate();
            return;
        }
        _slewForm = new ManualSlewForm(_engine, () => _settings) { Owner = this };
        _slewForm.FormClosed += (_, _) => _slewForm = null;
        _slewForm.Show(this);
    }

    // ---------------------------------------------------------------- settings & view

    private void OpenSettings(string? notice)
    {
        using var dlg = new SettingsForm(_settings, notice);
        if (TopMost) dlg.TopMost = true;
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        _settings = dlg.Result;
        TrySaveSettings();
        ApplyEngineConfig();
        ApplyRadioConfig();
        if (_radio.Snapshot.Connection == RotatorConnection.Connected)
            ShowMessage("Radio type, port or CI-V changes take effect the next time you connect the radio.");

        // Keep a connected rotator's travel limits in step with the settings.
        switch (_rotator)
        {
            case Gs232bRotator g:
                g.MaxAzimuth = _settings.MaxAzimuth;
                g.MaxElevation = _settings.MaxElevation;
                if (!g.PortName.Equals(_settings.ComPort, StringComparison.OrdinalIgnoreCase))
                    ShowMessage("COM port changed. Disconnect and connect again to use it.");
                break;
            case SimulatedRotator s:
                s.MaxAzimuth = _settings.MaxAzimuth;
                s.MaxElevation = _settings.MaxElevation;
                break;
        }
    }

    private void ApplyEngineConfig()
    {
        _engine.Configure(
            _settings.GetStation(),
            _settings.GetLimits(),
            new TrackingOptions(
                _settings.CommandThresholdDeg,
                _settings.LeadSeconds,
                _settings.PrePositionMinutes,
                _settings.ParkAfterPass,
                _settings.ParkAzimuth,
                _settings.ParkElevation));
    }

    private void SetProjection(MapProjection projection)
    {
        _settings.Projection = projection;
        _map.Projection = projection;
        _miMercator.Checked = projection == MapProjection.Mercator;
        _miPlanar.Checked = projection == MapProjection.Planar;
    }

    private void SetDarkMode(bool dark)
    {
        _settings.DarkMode = dark;
        ApplyTheme();
    }

    private void SetAlwaysOnTop(bool onTop)
    {
        _settings.AlwaysOnTop = onTop;
        TopMost = onTop;
        _miOnTop.Checked = onTop;
        if (_btnOnTop.Checked != onTop) _btnOnTop.Checked = onTop;
    }

    private void ApplyTheme()
    {
        _palette = Palette.For(_settings.DarkMode);
        var renderer = new FlatRenderer(_palette);

        BackColor = _palette.Window;
        ForeColor = _palette.Text;
        _tool.Renderer = renderer;
        _status.Renderer = renderer;
        _menu.DropDown.Renderer = renderer;
        _tool.BackColor = _status.BackColor = _palette.Panel;

        foreach (var combo in new[] { _satCombo, _speedCombo })
        {
            combo.ComboBox.FlatStyle = FlatStyle.Flat;
            combo.ComboBox.BackColor = _palette.Panel;
            combo.ComboBox.ForeColor = _palette.Text;
        }
        foreach (ToolStripItem item in _menu.DropDownItems) item.ForeColor = _palette.Text;
        _lblSim.ForeColor = _palette.Target;
        _lblSim.Tag = "keepcolor";

        _btnEnable.BackColor = Color.Empty; // re-applied on next tick
        _btnEnable.ForeColor = _palette.Text;
        _lblMsg.ForeColor = _palette.TextDim;

        _map.Palette = _palette;
        _freq.Palette = _palette;
        if (_passesForm is { IsDisposed: false }) _passesForm.ApplyPalette(_palette);
        _top.BackColor = _palette.Window;
        _azGauge.Palette = _palette;
        _elGauge.Palette = _palette;
        _miDark.Checked = _settings.DarkMode;
        _miMercator.Checked = _settings.Projection == MapProjection.Mercator;
        _miPlanar.Checked = _settings.Projection == MapProjection.Planar;

        UseDarkTitleBar(_settings.DarkMode);
        Invalidate(true);
    }

    private void TrySaveSettings()
    {
        try { SettingsStore.Save(_settings); }
        catch (Exception ex) { ShowMessage($"Couldn't save settings: {ex.Message}"); }
    }

    private void RestoreWindowBounds()
    {
        if (_settings.WindowBounds is not { Length: 4 } b) return;
        var rect = new Rectangle(b[0], b[1], b[2], b[3]);
        if (rect.Width < 200 || rect.Height < 150) return;
        if (!Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(rect))) return;
        StartPosition = FormStartPosition.Manual;
        Bounds = rect;
    }

    private void SaveWindowBounds()
    {
        var r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        _settings.WindowBounds = new[] { r.X, r.Y, r.Width, r.Height };
    }

    // ---------------------------------------------------------------- dark title bar (Windows 10 2004+ / 11)

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void UseDarkTitleBar(bool dark)
    {
        if (!IsHandleCreated) return;
        try
        {
            int value = dark ? 1 : 0;
            DwmSetWindowAttribute(Handle, 20, ref value, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
        }
        catch
        {
            // Older Windows: keep the default title bar.
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UseDarkTitleBar(_settings.DarkMode);
    }
}
