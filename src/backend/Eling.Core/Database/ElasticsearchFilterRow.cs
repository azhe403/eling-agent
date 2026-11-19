namespace Eling.Core.Database;

public sealed record ElasticsearchFilterRow(
    string Field,
    string Op,
    string? Value);
