using System.Text;

namespace SatTrack.Core.Radio;

/// <summary>A decoded ICOM CI-V frame: FE FE to from cmd [data...] FD.</summary>
public sealed record CivFrame(byte To, byte From, byte Command, byte[] Data)
{
    public const byte Preamble = 0xFE;
    public const byte End = 0xFD;
    public const byte Ok = 0xFB;
    public const byte Ng = 0xFA;

    public byte[] ToBytes()
    {
        var b = new byte[6 + Data.Length];
        b[0] = Preamble; b[1] = Preamble; b[2] = To; b[3] = From; b[4] = Command;
        Data.CopyTo(b, 5);
        b[^1] = End;
        return b;
    }

    public static string Hex(IEnumerable<byte> bytes) => string.Join(" ", bytes.Select(x => x.ToString("X2")));

    public string ToHex() => Hex(ToBytes());

    /// <summary>Frequency as 5 BCD bytes, least significant first (1 Hz resolution, up to 9.99 GHz).</summary>
    public static byte[] EncodeFrequency(long hz)
    {
        if (hz < 0 || hz > 9_999_999_999) throw new ArgumentOutOfRangeException(nameof(hz));
        var b = new byte[5];
        for (int i = 0; i < 5; i++)
        {
            int lo = (int)(hz % 10); hz /= 10;
            int hi = (int)(hz % 10); hz /= 10;
            b[i] = (byte)((hi << 4) | lo);
        }
        return b;
    }

    public static long DecodeFrequency(ReadOnlySpan<byte> bcd)
    {
        long hz = 0, mult = 1;
        foreach (byte x in bcd)
        {
            int lo = x & 0x0F, hi = x >> 4;
            if (lo > 9 || hi > 9) throw new FormatException("Invalid BCD frequency.");
            hz += lo * mult; mult *= 10;
            hz += hi * mult; mult *= 10;
        }
        return hz;
    }

    /// <summary>
    /// Pulls complete frames out of a receive buffer, removing consumed bytes. Leaves a partial
    /// frame in the buffer. Garbage and collision bytes before a preamble are discarded.
    /// </summary>
    public static List<CivFrame> Extract(List<byte> buffer)
    {
        var frames = new List<CivFrame>();
        while (true)
        {
            int start = -1;
            for (int i = 0; i + 1 < buffer.Count; i++)
                if (buffer[i] == Preamble && buffer[i + 1] == Preamble) { start = i; break; }
            if (start < 0)
            {
                // Keep a lone trailing FE (could be the start of the next preamble).
                if (buffer.Count > 0 && buffer[^1] == Preamble) buffer.RemoveRange(0, buffer.Count - 1);
                else buffer.Clear();
                return frames;
            }
            if (start > 0) buffer.RemoveRange(0, start);

            // Skip repeated preamble bytes (some radios send more than two).
            int p = 2;
            while (p < buffer.Count && buffer[p] == Preamble) p++;
            int end = buffer.IndexOf(End, p);
            if (end < 0) return frames; // incomplete

            int len = end - p; // to, from, cmd, data...
            if (len >= 3)
            {
                var data = buffer.GetRange(p + 3, len - 3).ToArray();
                frames.Add(new CivFrame(buffer[p], buffer[p + 1], buffer[p + 2], data));
            }
            buffer.RemoveRange(0, end + 1);
        }
    }

    /// <summary>Short human description used in the debug log.</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        switch (Command)
        {
            case 0x03 when Data.Length == 0: sb.Append("read frequency"); break;
            case 0x03 or 0x00 when Data.Length >= 5: sb.Append($"frequency {FormatMHz(DecodeFrequencySafe(Data))}"); break;
            case 0x05 when Data.Length >= 5: sb.Append($"set frequency {FormatMHz(DecodeFrequencySafe(Data))}"); break;
            case Ok: sb.Append("OK"); break;
            case Ng: sb.Append("NG (rejected)"); break;
            default: sb.Append($"command {Command:X2}"); break;
        }
        if (To == 0x00) sb.Append(" (broadcast)");
        return sb.ToString();
    }

    private static long DecodeFrequencySafe(byte[] d)
    {
        try { return DecodeFrequency(d.AsSpan(0, 5)); } catch { return -1; }
    }

    public static string FormatMHz(long hz) => hz < 0 ? "?" : $"{hz / 1e6:0.000000} MHz";
}
