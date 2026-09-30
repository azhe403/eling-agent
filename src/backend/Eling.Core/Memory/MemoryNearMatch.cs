namespace Eling.Core.Memory;

public readonly record struct MemoryNearMatch(
    MemoryId Id,
    string ContentPreview,
    double Score,
    MemoryType Type,
    IReadOnlyCollection<string> Tags);
