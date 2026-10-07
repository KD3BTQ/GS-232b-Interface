using System.Data;
using SatTrack.App.Controls;
using SatTrack.Core.Catalog;
using SatTrack.Core.Orbit;
using SatTrack.Core.Settings;
using SatTrack.Core.Tracking;

namespace SatTrack.App;

/// <summary>
/// Every pass of every satellite in the current list over the next N hours or days:
/// a sortable table and the ground tracks on a Mercator or planar map. Selecting rows
/// highlights their tracks; double-clicking a row selects that satellite in the main window.
/// </summary>
public sealed class PassesForm : Form
{
    private const int MaxFaintTraces = 600;

    private readonly OrbitalElementsService _elements;
    private readonly Func<SatelliteCatalog> _catalog;
    private readonly Func<AppSettings> _settings;
    private readonly Func<DateTime> _now;

    private readonly NumericUpDown _amount = new() { Minimum = 1, Maximum = 720, Value = 24, Width = 60, TextAlign = HorizontalAlignment.Right, Margin = new Padding(3, 4, 3, 3) };
    private readonly ComboBox _unit = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 70, Margin = new Padding(3, 4, 12, 3) };
    private readonly NumericUpDown _minEl = new() { Minimum = 0, Maximum = 89, Value = 0, Width = 50, TextAlign = HorizontalAlignment.Right, Margin = new Padding(3, 4, 12, 3) };
    private readonly CheckBox _local = new() { Text = "Local time", AutoSize = true, Margin = new Padding(3, 7, 12, 3) };
    private readonly Button _calc = new() { Text = "Calculate", AutoSize = true, Margin = new Padding(3, 3, 8, 3) };
    private readonly ProgressBar _progress = new() { Width = 120, Height = 16, Visible = false, Margin = new Padding(3, 7, 8, 3) };
    private readonly Label _status = new() { AutoSize = true, Margin = new Padding(3, 7, 3, 3) };

    private readonly MapView _map = new() { Dock = DockStyle.Fill, ShowInfoBox = false, ShowThemeChips = false };
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = true,
        RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        BorderStyle = BorderStyle.None,
        EnableHeadersVisualStyles = false,
    };
    private readonly SplitContainer _split = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };

    private readonly DataTable _table = new();
    private List<PassListItem> _items = new();
    private CancellationTokenSource? _cts;
    private Palette _palette;

    /// <summary>Raised when the user double-clicks a pass.</summary>
    public event Action<SatelliteEntry>? SatelliteChosen;

    public PassesForm(OrbitalElementsService elements, Func<SatelliteCatalog> catalog, Func<AppSettings> settings,
                      Func<DateTime> now, Palette palette, MapProjection projection)
    {
        _elements = elements;
        _catalog = catalog;
        _settings = settings;
        _now = now;
        _palette = palette;

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = $"{AppInfo.Name}: upcoming passes";
        Size = new Size(1000, 720);
        MinimumSize = new Size(560, 400);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        Font = new Font("Segoe UI", 9f);
        KeyPreview = true;

        _unit.Items.AddRange(new object[] { "hours", "days" });
        _unit.SelectedIndex = 0;

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6, 4, 6, 2), WrapContents = true };
        bar.Controls.AddRange(new Control[]
        {
            new Label { Text = "Passes in the next", AutoSize = true, Margin = new Padding(3, 7, 3, 3) },
            _amount, _unit,
            new Label { Text = "Highest point at least", AutoSize = true, Margin = new Padding(3, 7, 3, 3) },
            _minEl,
            new Label { Text = "°", AutoSize = true, Margin = new Padding(0, 7, 12, 3) },
            _local, _calc, _progress, _status,
        });

        _split.Panel1.Controls.Add(_map);
        _split.Panel2.Controls.Add(_grid);

        Controls.Add(_split);
        Controls.Add(bar);
        ResumeLayout(false);
        PerformLayout();

        BuildTable();
        _map.Projection = projection;
        _map.ProjectionChosen += p => _map.Projection = p;

        _calc.Click += async (_, _) => await CalculateAsync();
        _local.CheckedChanged += (_, _) => { UpdateHeaders(); _grid.Invalidate(); };
        _unit.SelectedIndexChanged += (_, _) => _amount.Maximum = _unit.SelectedIndex == 1 ? 30 : 720;
        _grid.SelectionChanged += (_, _) => UpdateTraces();
        _grid.CellFormatting += OnCellFormatting;
        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && ItemAt(e.RowIndex) is { } item) SatelliteChosen?.Invoke(item.Satellite);
        };

        ApplyPalette(palette);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _split.SplitterDistance = (int)(_split.Height * 0.55);
        await CalculateAsync();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _cts?.Cancel();
        base.OnFormClosed(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.F5) { _ = CalculateAsync(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ---------------------------------------------------------------- theme

    public void ApplyPalette(Palette p)
    {
        _palette = p;
        _map.Palette = p;
        _grid.BackgroundColor = p.Panel;
        _grid.GridColor = p.PanelEdge;
        _grid.DefaultCellStyle.BackColor = p.Panel;
        _grid.DefaultCellStyle.ForeColor = p.Text;
        _grid.DefaultCellStyle.SelectionBackColor = Blend(p.Panel, p.Target, 0.35);
        _grid.DefaultCellStyle.SelectionForeColor = p.Text;
        _grid.AlternatingRowsDefaultCellStyle.BackColor = Blend(p.Panel, p.Window, 0.5);
        _grid.ColumnHeadersDefaultCellStyle.BackColor = p.Window;
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = p.TextDim;
        _grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = p.Window;
        _grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        _split.BackColor = p.PanelEdge;
        _grid.Invalidate();
    }

    private static Color Blend(Color a, Color b, double t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    // ---------------------------------------------------------------- table

    private void BuildTable()
    {
        _table.Columns.Add("Index", typeof(int));
        _table.Columns.Add("Satellite", typeof(string));
        _table.Columns.Add("Rises", typeof(DateTime));
        _table.Columns.Add("Highest", typeof(double));
        _table.Columns.Add("Highest at", typeof(DateTime));
        _table.Columns.Add("Sets", typeof(DateTime));
        _table.Columns.Add("Duration", typeof(TimeSpan));
        _table.Columns.Add("Rise az", typeof(double));
        _table.Columns.Add("Set az", typeof(double));
        _grid.DataSource = _table;

        _grid.DataBindingComplete += (_, _) =>
        {
            if (_grid.Columns["Index"] is { } idx) idx.Visible = false;
            foreach (var name in new[] { "Rises", "Highest at", "Sets" })
                _grid.Columns[name]!.DefaultCellStyle.Format = "ddd HH:mm:ss";
            _grid.Columns["Highest"]!.DefaultCellStyle.Format = "0°";
            _grid.Columns["Rise az"]!.DefaultCellStyle.Format = "0°";
            _grid.Columns["Set az"]!.DefaultCellStyle.Format = "0°";
            _grid.Columns["Duration"]!.DefaultCellStyle.Format = @"h\:mm\:ss";
            foreach (var name in new[] { "Highest", "Rise az", "Set az", "Duration" })
                _grid.Columns[name]!.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            _grid.Columns["Satellite"]!.FillWeight = 140;
            UpdateHeaders();
        };
    }

    private void UpdateHeaders()
    {
        string zone = _local.Checked ? "local" : "UTC";
        foreach (var (name, text) in new[] { ("Rises", "Rises"), ("Highest at", "Highest at"), ("Sets", "Sets") })
            if (_grid.Columns[name] is { } c) c.HeaderText = $"{text} ({zone})";
        if (_grid.Columns["Highest"] is { } h) h.HeaderText = "Highest el";
    }

    private void OnCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.Value is DateTime t && _local.Checked)
        {
            e.Value = DateTime.SpecifyKind(t, DateTimeKind.Utc).ToLocalTime().ToString("ddd HH:mm:ss");
            e.FormattingApplied = true;
        }

        // Objects that never set (geostationary): say so instead of showing the search window.
        if (e.RowIndex >= 0 && ItemAt(e.RowIndex) is { Pass.NeverSets: true } item)
        {
            string col = _grid.Columns[e.ColumnIndex].Name;
            if (col is "Rises" or "Sets" or "Duration" or "Highest at" or "Set az")
            {
                e.Value = col == "Rises" ? "always up" : "";
                e.FormattingApplied = true;
            }
        }
    }

    private PassListItem? ItemAt(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= _grid.Rows.Count) return null;
        if (_grid.Rows[rowIndex].DataBoundItem is DataRowView v && v["Index"] is int i && i < _items.Count)
            return _items[i];
        return null;
    }

    // ---------------------------------------------------------------- calculation

    private async Task CalculateAsync()
    {
        var station = _settings().GetStation();
        if (station is null)
        {
            _status.Text = "Set your location in Settings first.";
            return;
        }

        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;

        var sats = _catalog().Satellites.ToList();
        var from = _now();
        var span = _unit.SelectedIndex == 1 ? TimeSpan.FromDays((double)_amount.Value) : TimeSpan.FromHours((double)_amount.Value);
        var to = from + span;
        double minEl = (double)_minEl.Value;

        _calc.Enabled = false;
        _progress.Maximum = Math.Max(1, sats.Count);
        _progress.Value = 0;
        _progress.Visible = true;
        _status.Text = $"Searching {sats.Count} satellites…";

        var progress = new Progress<int>(n => { if (!cts.IsCancellationRequested) _progress.Value = Math.Min(n, _progress.Maximum); });

        try
        {
            var result = await Task.Run(() => PassSearch.Run(sats, _elements, station, from, to, minEl, progress, cts.Token));
            if (cts.IsCancellationRequested || IsDisposed) return;
            Show(result, from, to);
        }
        catch (OperationCanceledException)
        {
            // A newer calculation replaced this one.
        }
        catch (Exception ex)
        {
            _status.Text = $"Couldn't calculate passes: {ex.Message}";
        }
        finally
        {
            if (_cts == cts && !IsDisposed)
            {
                _calc.Enabled = true;
                _progress.Visible = false;
            }
        }
    }

    private void Show(PassSearchResult result, DateTime from, DateTime to)
    {
        _items = result.Passes.ToList();

        _grid.SuspendLayout();
        _table.BeginLoadData();
        _table.Rows.Clear();
        for (int i = 0; i < _items.Count; i++)
        {
            var p = _items[i].Pass;
            _table.Rows.Add(i, _items[i].Satellite.Name, p.AosUtc, Math.Round(p.MaxElevation, 1), p.MaxElevationUtc,
                            p.LosUtc, p.Duration, Math.Round(p.AosAzimuth), Math.Round(p.LosAzimuth));
        }
        _table.EndLoadData();
        _grid.ResumeLayout();
        _grid.ClearSelection();

        string window = to - from >= TimeSpan.FromDays(1) ? $"{(to - from).TotalDays:0.#} days" : $"{(to - from).TotalHours:0.#} hours";
        string text = $"{_items.Count:N0} passes of {result.SatellitesSearched - result.SkippedSatellites.Count} satellites in the next {window}.";
        if (result.SkippedSatellites.Count > 0)
            text += $" {result.SkippedSatellites.Count} without orbital data skipped.";
        text += _items.Count > MaxFaintTraces
            ? " Select rows to show their tracks on the map."
            : " Select rows to highlight; double-click to pick that satellite.";
        _status.Text = text;

        UpdateTraces();
    }

    private void UpdateTraces()
    {
        var selected = new HashSet<int>();
        foreach (DataGridViewRow row in _grid.SelectedRows)
            if (row.DataBoundItem is DataRowView v && v["Index"] is int i) selected.Add(i);

        bool drawAll = _items.Count <= MaxFaintTraces;
        var traces = new List<MapTrace>();
        for (int i = 0; i < _items.Count; i++)
        {
            bool hi = selected.Contains(i);
            if (!hi && !drawAll) continue;
            var item = _items[i];
            if (item.Track.Count < 2) continue;
            string label = $"{item.Satellite.Name} {item.Pass.AosUtc:HH:mm}Z";
            traces.Add(new MapTrace(item.Track, hi, hi ? label : null));
        }

        _map.Traces = traces;
        _map.Snapshot = new TrackerSnapshot
        {
            UtcNow = _now(),
            Station = _settings().GetStation(),
            Limits = _settings().GetLimits(),
        };
    }
}
