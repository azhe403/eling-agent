namespace Eling.Core.Memory;

/// <summary>One scope-chain level's memories for level-grouped merging.</summary>
public sealed record MemoryLevel(string ProjectRoot, IReadOnlyCollection<Memory> Memories);
