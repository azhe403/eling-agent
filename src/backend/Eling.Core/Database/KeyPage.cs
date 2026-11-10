namespace Eling.Core.Database;

public sealed record KeyPage(
    string Prefix,
    IReadOnlyList<KeyNode> Nodes,
    IReadOnlyList<KeyLeaf> Keys,
    long Cursor,
    int PageOffset,
    bool Complete,
    int Scanned,
    bool FromCache = false,
    DateTimeOffset? CachedAt = null);