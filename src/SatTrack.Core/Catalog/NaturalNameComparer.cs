namespace SatTrack.Core.Catalog;

/// <summary>
/// Alphabetical order that treats runs of digits as numbers, so "AO-7" comes before "AO-27"
/// and "AO-123", and "TEVEL2-9" before "TEVEL2-10". Case-insensitive.
/// </summary>
public sealed class NaturalNameComparer : IComparer<string>
{
    public static readonly NaturalNameComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                int si = i, sj = j;
                while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                while (j < y.Length && char.IsAsciiDigit(y[j])) j++;
                var a = x.AsSpan(si, i - si).TrimStart('0');
                var b = y.AsSpan(sj, j - sj).TrimStart('0');
                if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
                int c = a.CompareTo(b, StringComparison.Ordinal);
                if (c != 0) return c;
                // Same value: fewer leading zeros first ("7" before "07").
                int z = (i - si).CompareTo(j - sj);
                if (z != 0) return z;
            }
            else
            {
                int c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                if (c != 0) return c;
                i++; j++;
            }
        }
        return (x.Length - i).CompareTo(y.Length - j);
    }
}
