namespace Eling.Core.Database;

/// <summary>
/// Pure helper: given a scan prefix and a scanned key, splits the key's
/// remainder on ':' into the first segment (a node) or, when there is no
/// further colon, a leaf. See spec §5.1.
/// </summary>
public static class KeyTreeSegmenter
{
    public sealed record SegmentResult(
        string? NodeSegment,
        string? NodePrefix,
        bool IsLeaf);

    /// <summary>
    /// Returns the node segment + node prefix when the key has a further
    /// segment under <paramref name="prefix"/>, or marks it as a leaf.
    /// Empty segments (trailing/leading/double colons) are never nodes.
    /// </summary>
    public static SegmentResult Segment(string prefix, string key)
    {
        if (!key.StartsWith(prefix, StringComparison.Ordinal))
        {
            return new SegmentResult(null, null, true);
        }

        var rest = key.Substring(prefix.Length);
        var colon = rest.IndexOf(':');
        if (colon < 0)
        {
            // No further colon: the key itself is a leaf under prefix.
            return new SegmentResult(null, null, true);
        }

        var segment = rest.Substring(0, colon);
        if (string.IsNullOrEmpty(segment))
        {
            // Empty segment (leading/double colon): treat as a leaf.
            return new SegmentResult(null, null, true);
        }

        return new SegmentResult(segment, prefix + segment + ":", false);
    }

    /// <summary>
    /// True when the key is a direct leaf under prefix (no colon in remainder).
    /// </summary>
    public static bool IsLeaf(string prefix, string key) =>
        Segment(prefix, key).IsLeaf;
}