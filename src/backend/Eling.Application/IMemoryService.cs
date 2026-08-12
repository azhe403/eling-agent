using Eling.Core;
using Eling.Index;

namespace Eling.Application;

public interface IMemoryService
{
    Task<Memory> SaveAsync(Memory memory);
    Task<Memory?> GetByIdAsync(MemoryId id);
    Task<bool> DeleteAsync(MemoryId id);
    Task<IReadOnlyCollection<Memory>> ListAllAsync();
    Task<IReadOnlyCollection<MemorySearchResult>> SearchAsync(string query);
    Task RebuildIndexAsync();
}
