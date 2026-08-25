using Eling.Core;

namespace Eling.Application;

public interface IMemoryIndex
{
    Task IndexAsync(Memory memory);

    Task RemoveAsync(MemoryId id);

    Task RebuildAsync(IEnumerable<Memory> memories);

    Task<IReadOnlyCollection<MemorySearchResult>> SearchAsync(string query);
}
