namespace Eling.Core;

public sealed record MemoryReference(
    MemoryId Id,
    MemoryScopeKind Scope,
    string? ProjectRoot = null)
{
    public static MemoryReference ForGlobal(MemoryId id) => new(id, MemoryScopeKind.Global, null);

    public static MemoryReference ForProject(MemoryId id, string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        return new(id, MemoryScopeKind.Project, Path.GetFullPath(projectRoot));
    }
}
