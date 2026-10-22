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
}