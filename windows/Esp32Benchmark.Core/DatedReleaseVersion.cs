using System.Globalization;

namespace Esp32Benchmark.Core;

public readonly record struct DatedReleaseVersion(DateOnly ReleaseDate, int Sequence)
    : IComparable<DatedReleaseVersion>
{
    public static bool TryParseTag(string? tag, out DatedReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(tag) || tag[0] != 'V')
        {
            return false;
        }

        var parts = tag[1..].Split('.');
        if (parts.Length != 4 ||
            parts[0].Length != 4 ||
            parts[1].Length != 2 ||
            parts[2].Length != 2 ||
            !DateOnly.TryParseExact(
                string.Join(".", parts.Take(3)),
                "yyyy.MM.dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var releaseDate) ||
            !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) ||
            sequence < 1)
        {
            return false;
        }

        version = new DatedReleaseVersion(releaseDate, sequence);
        return true;
    }

    public int CompareTo(DatedReleaseVersion other)
    {
        var dateComparison = ReleaseDate.CompareTo(other.ReleaseDate);
        return dateComparison != 0 ? dateComparison : Sequence.CompareTo(other.Sequence);
    }

    public override string ToString() =>
        $"V{ReleaseDate.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture)}.{Sequence}";
}
