namespace Eling.Core;

public sealed record ScopedMemory(
    Memory Memory,
    MemoryScopeKind Scope,
    string? ProjectRoot = null)
{
    public MemoryId Id => Memory.Id;
}

public sealed record ScopedSearchResult(
    MemoryId Id,
    double Rank,
    MemoryScopeKind Scope,
    string? ProjectRoot = null);
