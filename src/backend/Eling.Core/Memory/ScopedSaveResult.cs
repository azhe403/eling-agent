namespace Eling.Core.Memory;

public readonly record struct ScopedSaveResult(ScopedMemory Scoped, SaveAction Action, ScopedMemory? Previous = null)
{
    public Memory Memory => Scoped.Memory;
    public MemoryScopeKind Scope => Scoped.Scope;
    public string? ProjectRoot => Scoped.ProjectRoot;
    public MemoryId Id => Scoped.Id;

    public static implicit operator ScopedMemory(ScopedSaveResult result) => result.Scoped;
    public static implicit operator Memory(ScopedSaveResult result) => result.Memory;
}
