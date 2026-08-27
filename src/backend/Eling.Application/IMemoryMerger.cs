using Eling.Core;

namespace Eling.Application;

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
}
