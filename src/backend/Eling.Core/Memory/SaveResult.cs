namespace Eling.Core.Memory;

public readonly record struct SaveResult(Memory Memory, SaveAction Action, Memory? Previous = null)
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
