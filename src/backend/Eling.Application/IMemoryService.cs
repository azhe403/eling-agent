using Eling.Core;
using Eling.Application;

namespace Eling.Application;

public interface IMemoryService
{
    Task<SaveResult> SaveAsync(Memory memory);
    Task<Memory?> GetByIdAsync(MemoryId id);
    Task<Memory?> UpdateAsync(MemoryId id, string? content = null, MemoryType? type = null, string[]? tags = null, string? source = null, MemoryStatus? status = null);
    Task<bool> DeleteAsync(MemoryId id);
    Task<IReadOnlyCollection<Memory>> ListAllAsync();
    Task<IReadOnlyCollection<MemorySearchResult>> SearchAsync(string query);
    Task RebuildIndexAsync();
}
