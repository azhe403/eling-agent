namespace Eling.Core.Database;

public sealed record KeyLeaf(
    string Key,
    string Type,
    long TtlSeconds,
    DateTimeOffset? ExpiresAt = null,
    /// <summary>
    /// True when the key was seen in an earlier scan of this page and is no
    /// longer in the keyspace. The row stays in the tree, marked, so a key does
    /// not vanish on its own while a reader is watching it.
    /// </summary>
    bool Expired = false);
