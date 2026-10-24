using System.Text.Json;

namespace Eling.Core.Database;

/// <summary>
/// The cache entry shapes. Stored as JSON; the cells are kept as
/// <see cref="System.Text.Json.JsonElement"/> so the file stays plain JSON, and
/// unwrapped on read via <see cref="Eling.Core.Serialization.JsonCell"/>.
/// </summary>
public sealed record CachedQueryEntry(
    string ConnectionId,
    string? Database,
    int MaxRows,
    string SqlHash,
    DateTimeOffset CachedAt,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<System.Text.Json.JsonElement>> Rows,
    bool Truncated);

public sealed record CachedSchemaEntry(
    string ConnectionId,
    string? Database,
    DateTimeOffset CachedAt,
    DatabaseSchemaInfo Schema);

public sealed record CachedDatabaseListEntry(
    string ConnectionId,
    DateTimeOffset CachedAt,
    IReadOnlyList<string> Databases);