namespace Eling.Core.Memory;

public sealed record ScopedMemory(
    Memory Memory,
    MemoryScopeKind Scope,
    string? ProjectRoot = null)
{
    public MemoryId Id => Memory.Id;
}
