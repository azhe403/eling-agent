namespace Eling.Core.Database;

public sealed record KeyLeaf(
    string Key,
    string Type,
    long TtlSeconds);