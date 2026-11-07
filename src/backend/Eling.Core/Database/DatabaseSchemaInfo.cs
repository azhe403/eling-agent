namespace Eling.Core.Database;

public sealed record ColumnSchema(
    string Name,
    string DataType,
    bool IsNullable,
    bool IsPrimaryKey);

public sealed record ForeignKeySchema(
    string Name,
    string Column,
    string ReferencesTable,
    string ReferencesColumn);

public sealed record IndexSchema(
    string Name,
    bool IsUnique,
    IReadOnlyList<string> Columns);

public sealed record TableSchema(
    string Name,
    string Kind,
    IReadOnlyList<ColumnSchema> Columns,
    IReadOnlyList<ForeignKeySchema> ForeignKeys,
    IReadOnlyList<IndexSchema> Indexes);

public sealed record DatabaseSchemaInfo(IReadOnlyList<TableSchema> Tables)
{
    /// <summary>
    /// The databases on the server, for a server-level connection - one
    /// registered without a database. Empty when the connection names one.
    /// </summary>
    public IReadOnlyList<string> Databases { get; init; } = [];

    public bool FromCache => Source != CacheSources.Live;
    public DateTimeOffset? CachedAt { get; init; }

    /// <summary>
    /// How this answer was obtained: <c>"live"</c> when the database answered,
    /// <c>"cached"</c> when a snapshot answered before the database was read,
    /// <c>"offline"</c> when the database could not be reached and the snapshot
    /// answered instead.
    /// </summary>
    public string Source { get; init; } = CacheSources.Live;

    /// <summary>When the cached snapshot stops being served at all.</summary>
    public DateTimeOffset? CacheExpiresAt { get; init; }

    /// <summary>Seconds until <see cref="CacheExpiresAt"/>, floored at zero.</summary>
    public long? CacheTtlSeconds { get; init; }
}