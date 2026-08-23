namespace Eling.Core;

using System.Collections.ObjectModel;

public class Intention
{
    public MemoryId Id { get; }
    public string Description { get; }
    public TriggerType TriggerType { get; }
    public MemoryStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? Source { get; set; }
    public IReadOnlyCollection<string> Tags { get; set; }

    public Intention(
        string description,
        TriggerType triggerType,
        IEnumerable<string>? tags = null,
        string? source = null,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? updatedAt = null,
        DateTimeOffset? expiresAt = null)
    {
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(triggerType);

        Id = MemoryId.NewId();
        Description = description;
        TriggerType = triggerType;
        Status = MemoryStatus.Active;
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow;
        UpdatedAt = updatedAt ?? CreatedAt;
        ExpiresAt = expiresAt;
        Source = source;
        Tags = tags?.ToList().AsReadOnly() ?? (IReadOnlyCollection<string>)Array.Empty<string>();
    }
}