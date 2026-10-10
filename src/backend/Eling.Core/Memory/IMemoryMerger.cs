namespace Eling.Core.Memory;

public interface IMemoryMerger
{
    IReadOnlyCollection<ScopedMemory> MergeLists(
        IReadOnlyCollection<Memory> projectMemories,
        IReadOnlyCollection<Memory> globalMemories,
        string? projectRoot);

    IReadOnlyCollection<ScopedSearchResult> MergeSearchResults(
        IReadOnlyCollection<MemorySearchResult> projectResults,
        IReadOnlyCollection<MemorySearchResult> globalResults,
        string? projectRoot);

    IReadOnlyCollection<ScopedMemory> MergeLists(
        IReadOnlyList<MemoryLevel> levels,
        IReadOnlyCollection<Memory> globalMemories);

    IReadOnlyCollection<ScopedSearchResult> MergeSearchResults(
        IReadOnlyList<SearchResultLevel> levels,
        IReadOnlyCollection<MemorySearchResult> globalResults);

    IReadOnlyCollection<ScopedMemory> MergeLists(
        IReadOnlyList<MemoryLevel> levels,
        IReadOnlyCollection<Memory> localMemories,
        string? localRoot,
        IReadOnlyCollection<Memory> globalMemories);

    IReadOnlyCollection<ScopedSearchResult> MergeSearchResults(
        IReadOnlyList<SearchResultLevel> levels,
        IReadOnlyCollection<MemorySearchResult> localResults,
        string? localRoot,
        IReadOnlyCollection<MemorySearchResult> globalResults);
}

