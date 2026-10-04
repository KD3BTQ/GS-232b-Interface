using System.Globalization;
using SatTrack.Core.Rotator;

namespace SatTrack.App;

/// <summary>
/// Live view of all serial traffic with the controller. Uses a virtual list so it stays
/// fast with hundreds of thousands of lines. Save writes the whole session to a .log file.
/// </summary>
public sealed class DebugForm : Form
{
    private readonly CommLog _log;
    private readonly List<CommLogEntry> _shown = new();
    private long _readTotal;

    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        VirtualMode = true,
        FullRowSelect = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
        HideSelection = false,
        GridLines = false,
    };

    private readonly CheckBox _autoScroll = new() { Text = "Auto-scroll", Checked = true, AutoSize = true, Margin = new Padding(3, 7, 12, 3) };
    private readonly CheckBox _hidePolls = new() { Text = "Hide position polls (C2)", AutoSize = true, Margin = new Padding(3, 7, 12, 3) };
    private readonly Label _count = new() { AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };

    public DebugForm(CommLog log)
    {
        _log = log;

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = $"{AppInfo.Name}: serial debug";
        Size = new Size(760, 420);
        MinimumSize = new Size(420, 200);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        Font = new Font("Segoe UI", 9f);

        _list.Columns.Add("Time (UTC)", 175);
        _list.Columns.Add("", 48);
        _list.Columns.Add("Data", 480);
        _list.Font = new Font("Consolas", 9f);
        _list.RetrieveVirtualItem += OnRetrieveItem;
        _list.KeyDown += OnListKeyDown;

        var save = new Button { Text = "Save log…", AutoSize = true };
        var copy = new Button { Text = "Copy selected", AutoSize = true };
        var clear = new Button { Text = "Clear", AutoSize = true };
        save.Click += (_, _) => SaveLog();
        copy.Click += (_, _) => CopySelected();
        clear.Click += (_, _) => ClearLog();
        _hidePolls.CheckedChanged += (_, _) => Rebuild();

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4, 4, 4, 2), WrapContents = true };
        bar.Controls.AddRange(new Control[] { save, copy, clear, _autoScroll, _hidePolls, _count });

        Controls.Add(_list);
        Controls.Add(bar);
        ResumeLayout(false);
        PerformLayout();

        _timer.Tick += (_, _) => Pull();
        Rebuild();
        _timer.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        base.OnFormClosed(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_list.Columns.Count == 3)
            _list.Columns[2].Width = Math.Max(150, _list.ClientSize.Width - _list.Columns[0].Width - _list.Columns[1].Width - 4);
    }

    private void Pull()
    {
        var added = _log.GetSince(_readTotal, out _readTotal);
        if (added.Count == 0) { UpdateCount(); return; }

        foreach (var e in added)
            if (!_hidePolls.Checked || !e.IsPositionPoll) _shown.Add(e);

        UpdateList(scroll: true);
    }

    private void Rebuild()
    {
        _shown.Clear();
        _readTotal = 0;
        var all = _log.GetSince(0, out _readTotal);
        foreach (var e in all)
            if (!_hidePolls.Checked || !e.IsPositionPoll) _shown.Add(e);
        UpdateList(scroll: true);
    }

    private void UpdateList(bool scroll)
    {
        _list.VirtualListSize = _shown.Count;
        if (scroll && _autoScroll.Checked && _shown.Count > 0)
            _list.EnsureVisible(_shown.Count - 1);
        _list.Invalidate();
        UpdateCount();
    }

    private void UpdateCount()
    {
        string text = _hidePolls.Checked ? $"{_shown.Count:N0} shown" : $"{_shown.Count:N0} lines";
        if (_count.Text != text) _count.Text = text;
    }

    private void OnRetrieveItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        if (e.ItemIndex < 0 || e.ItemIndex >= _shown.Count)
        {
            e.Item = new ListViewItem(new[] { "", "", "" });
            return;
        }
        var entry = _shown[e.ItemIndex];
        var item = new ListViewItem(new[]
        {
            entry.Utc.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            entry.DirectionLabel,
            entry.Text,
        });
        item.ForeColor = entry.Direction switch
        {
            CommDirection.Tx => Color.FromArgb(0x0B, 0x5F, 0x9E),
            CommDirection.Rx => Color.FromArgb(0x2E, 0x6B, 0x2F),
            CommDirection.Error => Color.Firebrick,
            _ => SystemColors.GrayText,
        };
        e.Item = item;
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.C) { CopySelected(); e.Handled = true; }
        if (e.Control && e.KeyCode == Keys.A)
        {
            for (int i = 0; i < _shown.Count; i++) _list.SelectedIndices.Add(i);
            e.Handled = true;
        }
    }

    private void CopySelected()
    {
        if (_list.SelectedIndices.Count == 0) return;
        var lines = _list.SelectedIndices.Cast<int>().OrderBy(i => i)
                         .Where(i => i < _shown.Count)
                         .Select(i => _shown[i].ToString());
        Clipboard.SetText(string.Join(Environment.NewLine, lines));
    }

    private void ClearLog()
    {
        _log.Clear();
        Rebuild();
    }

    private void SaveLog()
    {
        using var dlg = new SaveFileDialog
        {
            Title = "Save serial log",
            Filter = "Log file (*.log)|*.log|Text file (*.txt)|*.txt",
            FileName = $"GS232B-serial-{DateTime.Now:yyyyMMdd-HHmmss}.log",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            _log.SaveTo(dlg.FileName, AppInfo.Name);
            MessageBox.Show(this, $"Saved the full session log to {dlg.FileName}.", "Saved",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Couldn't save the log", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
