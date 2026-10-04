using System.Globalization;
using System.Text;

namespace SatTrack.Core.Rotator;

public enum CommDirection { Tx, Rx, Info, Error }

public readonly record struct CommLogEntry(DateTime Utc, CommDirection Direction, string Text)
{
    public string DirectionLabel => Direction switch
    {
        CommDirection.Tx => "TX",
        CommDirection.Rx => "RX",
        CommDirection.Error => "ERR",
        _ => "INFO",
    };

    /// <summary>True for routine position polling (C2 and its AZ=/EL= reply), which can be hidden.</summary>
    public bool IsPositionPoll =>
        (Direction == CommDirection.Tx && Text.StartsWith("C2", StringComparison.Ordinal)) ||
        (Direction == CommDirection.Rx && Text.Contains("AZ=", StringComparison.Ordinal) && Text.Contains("EL=", StringComparison.Ordinal));

    public override string ToString() =>
        $"{Utc.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}Z  {DirectionLabel,-4} {Text}";
}

/// <summary>
/// Every byte sent to and received from the rotator controller, plus connection events.
/// Thread-safe; keeps the most recent <see cref="Capacity"/> entries for the whole session.
/// Control characters are shown escaped (\r, \n) so line endings are visible.
/// </summary>
public sealed class CommLog
{
    private readonly object _lock = new();
    private readonly List<CommLogEntry> _entries = new();
    private long _dropped;

    /// <summary>Entries kept in memory. About a week of continuous 1-second polling.</summary>
    public int Capacity { get; set; } = 1_000_000;

    /// <summary>Total entries ever added (keeps counting after old ones are dropped).</summary>
    public long TotalAdded { get { lock (_lock) return _dropped + _entries.Count; } }

    public long Dropped { get { lock (_lock) return _dropped; } }

    public DateTime StartedUtc { get; } = DateTime.UtcNow;

    public void Tx(string text) => Add(CommDirection.Tx, text);
    public void Rx(string text) => Add(CommDirection.Rx, text);
    public void Info(string text) => Add(CommDirection.Info, text);
    public void Error(string text) => Add(CommDirection.Error, text);

    public void Add(CommDirection direction, string text)
    {
        var entry = new CommLogEntry(DateTime.UtcNow, direction, Escape(text));
        lock (_lock)
        {
            _entries.Add(entry);
            if (_entries.Count > Capacity)
            {
                int remove = _entries.Count - Capacity + Capacity / 10; // trim in chunks
                _entries.RemoveRange(0, remove);
                _dropped += remove;
            }
        }
    }

    /// <summary>Entries with a sequence number at or after <paramref name="fromTotal"/>.</summary>
    public List<CommLogEntry> GetSince(long fromTotal, out long newTotal)
    {
        lock (_lock)
        {
            newTotal = _dropped + _entries.Count;
            long start = Math.Max(0, fromTotal - _dropped);
            if (start >= _entries.Count) return new List<CommLogEntry>();
            return _entries.GetRange((int)start, _entries.Count - (int)start);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _dropped += _entries.Count;
            _entries.Clear();
        }
    }

    public void SaveTo(string path, string title)
    {
        List<CommLogEntry> copy;
        long dropped;
        lock (_lock)
        {
            copy = new List<CommLogEntry>(_entries);
            dropped = _dropped;
        }

        using var w = new StreamWriter(path, false, new UTF8Encoding(false));
        w.WriteLine($"# {title}: serial communication log");
        w.WriteLine($"# Saved {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z, session started {StartedUtc:yyyy-MM-dd HH:mm:ss}Z");
        w.WriteLine($"# {copy.Count} entries" + (dropped > 0 ? $" ({dropped} earlier entries were cleared or dropped)" : ""));
        w.WriteLine("# TX = sent to controller, RX = received, \\r and \\n are carriage return and line feed");
        w.WriteLine();
        foreach (var e in copy) w.WriteLine(e.ToString());
    }

    public static string Escape(string s)
    {
        if (s.Length == 0) return "(nothing)";
        var sb = new StringBuilder(s.Length + 8);
        foreach (char c in s)
        {
            switch (c)
            {
                case '\r': sb.Append("\\r"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20 || c == 0x7F) sb.Append($"\\x{(int)c:X2}");
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
