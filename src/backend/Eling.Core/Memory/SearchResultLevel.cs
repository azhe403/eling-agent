namespace Eling.Core.Memory;

/// <summary>One scope-chain level's search results for level-grouped merging.</summary>
public sealed record SearchResultLevel(string ProjectRoot, IReadOnlyCollection<MemorySearchResult> Results);
