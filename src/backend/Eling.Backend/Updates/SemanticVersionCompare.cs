namespace Eling.Backend.Updates;

/// <summary>
/// Lenient semantic-version precedence for release tags. Strips a leading
/// <c>v</c> and any <c>+</c> build metadata, compares the numeric core per
/// segment, and orders pre-release suffixes so a stable tag outranks any
/// pre-release on the same core while numeric suffixes compare numerically
/// (<c>pre.12</c> beats <c>pre.9</c>). Unparsable parts degrade to zero
/// instead of throwing, because tag formats evolve outside this repo.
/// </summary>
public static class SemanticVersionCompare
{
    /// <summary>
    /// Compares two versions: negative when <paramref name="left"/> is older,
    /// zero when equal, positive when newer.
    /// </summary>
    public static int Compare(string left, string right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        var core = CompareNumericSequences(CoreParts(left), CoreParts(right));
        if (core != 0)
        {
            return core;
        }

        return CompareSuffixes(SuffixParts(left), SuffixParts(right));
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> counts as newer than <paramref name="current"/>.
    /// </summary>
    public static bool IsNewer(string candidate, string current)
        => Compare(candidate, current) > 0;

    private static string Stripped(string version)
    {
        var trimmed = version.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
        {
            trimmed = trimmed[1..];
        }

        var metadata = trimmed.IndexOf('+');
        return metadata < 0 ? trimmed : trimmed[..metadata];
    }

    private static int[] CoreParts(string version)
    {
        var core = Stripped(version);
        var dash = core.IndexOf('-');
        if (dash >= 0)
        {
            core = core[..dash];
        }

        return core.Split('.').Select(OrZero).ToArray();
    }

    private static string[] SuffixParts(string version)
    {
        var stripped = Stripped(version);
        var dash = stripped.IndexOf('-');
        if (dash < 0)
        {
            return [];
        }

        return stripped[(dash + 1)..].Split('.');
    }

    private static int OrZero(string part)
        => int.TryParse(part, out var value) && value >= 0 ? value : 0;

    private static int CompareNumericSequences(int[] left, int[] right)
    {
        var length = Math.Max(left.Length, right.Length);
        for (var i = 0; i < length; i++)
        {
            var l = i < left.Length ? left[i] : 0;
            var r = i < right.Length ? right[i] : 0;
            if (l != r)
            {
                return l.CompareTo(r);
            }
        }

        return 0;
    }

    private static int CompareSuffixes(string[] left, string[] right)
    {
        // A stable version (no suffix) outranks any pre-release on the same core.
        if (left.Length == 0 && right.Length == 0)
        {
            return 0;
        }

        if (left.Length == 0)
        {
            return 1;
        }

        if (right.Length == 0)
        {
            return -1;
        }

        var length = Math.Max(left.Length, right.Length);
        for (var i = 0; i < length; i++)
        {
            if (i >= left.Length)
            {
                return -1;
            }

            if (i >= right.Length)
            {
                return 1;
            }

            var compared = CompareSuffixPart(left[i], right[i]);
            if (compared != 0)
            {
                return compared;
            }
        }

        return 0;
    }

    private static int CompareSuffixPart(string left, string right)
    {
        var leftIsNumeric = int.TryParse(left, out var l);
        var rightIsNumeric = int.TryParse(right, out var r);
        if (leftIsNumeric && rightIsNumeric)
        {
            return l.CompareTo(r);
        }

        // Numeric identifiers rank below alphanumeric ones (semver rule).
        if (leftIsNumeric)
        {
            return -1;
        }

        if (rightIsNumeric)
        {
            return 1;
        }

        return string.Compare(left, right, StringComparison.Ordinal);
    }
}
