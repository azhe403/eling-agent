namespace Eling.Core;

public readonly record struct SaveResult(Memory Memory, SaveAction Action)
{
    public MemoryId Id => Memory.Id;
    public MemoryType Type => Memory.Type;
    public MemoryStatus Status => Memory.Status;
    public string Content => Memory.Content;
    public IReadOnlyCollection<string> Tags => Memory.Tags;
    public DateTimeOffset CreatedAt => Memory.CreatedAt;
    public DateTimeOffset UpdatedAt => Memory.UpdatedAt;
    public string? Source => Memory.Source;

    public static implicit operator Memory(SaveResult result) => result.Memory;
}

public readonly record struct ScopedSaveResult(ScopedMemory Scoped, SaveAction Action)
{
    public Memory Memory => Scoped.Memory;
    public MemoryScopeKind Scope => Scoped.Scope;
    public string? ProjectRoot => Scoped.ProjectRoot;
    public MemoryId Id => Scoped.Id;

    public static implicit operator ScopedMemory(ScopedSaveResult result) => result.Scoped;
    public static implicit operator Memory(ScopedSaveResult result) => result.Memory;
}
