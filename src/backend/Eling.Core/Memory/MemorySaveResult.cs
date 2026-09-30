namespace Eling.Core.Memory;

public readonly record struct MemorySaveResult(
    Memory Memory,
    SaveAction Action,
    Memory? Previous = null,
    IReadOnlyCollection<MemoryNearMatch>? NearMatches = null,
    string? Reason = null,
    double? MatchScore = null)
{
    public MemoryId Id => Memory.Id;
    public MemoryType Type => Memory.Type;
    public MemoryStatus Status => Memory.Status;
    public string Content => Memory.Content;
    public IReadOnlyCollection<string> Tags => Memory.Tags;
    public DateTimeOffset CreatedAt => Memory.CreatedAt;
    public DateTimeOffset UpdatedAt => Memory.UpdatedAt;
    public string? Source => Memory.Source;
    public IReadOnlyCollection<MemoryNearMatch> NearMatches { get; init; } = NearMatches ?? [];

    public static implicit operator Memory(MemorySaveResult result) => result.Memory;
}
