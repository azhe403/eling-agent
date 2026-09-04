namespace Eling.Core;

public class Memory
{
    public MemoryId Id { get; }
    public MemoryType Type { get; }
    public MemoryStatus Status { get; set; }
    public string Content { get; set; }
    public IReadOnlyCollection<string> Tags { get; set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? Source { get; set; }

    public Memory(
        MemoryType type,
        string content,
        IEnumerable<string>? tags = null,
        string? source = null,
        MemoryStatus status = MemoryStatus.Active,
        MemoryId? id = null,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? updatedAt = null)
    {
        ArgumentNullException.ThrowIfNull(content);

        Id = id ?? MemoryId.NewId();
        Type = type;
        Status = status;
        Content = content;
        Tags = tags?.ToList().AsReadOnly() ?? (IReadOnlyCollection<string>)Array.Empty<string>();
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow;
        UpdatedAt = updatedAt ?? CreatedAt;
        Source = source;
    }
}
