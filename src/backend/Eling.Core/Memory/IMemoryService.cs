namespace Eling.Core.Memory;

public interface IMemoryService
{
    Task<SaveResult> SaveAsync(Memory memory);
    Task<Memory?> FindActiveSimilarAsync(Memory memory, double? threshold = null) => Task.FromResult<Memory?>(null);
    Task<Memory?> GetByIdAsync(MemoryId id);
    Task<Memory?> UpdateAsync(MemoryId id, string? content = null, MemoryType? type = null, string[]? tags = null, string? source = null, MemoryStatus? status = null);
    Task<bool> DeleteAsync(MemoryId id);
    Task<IReadOnlyCollection<Memory>> ListAllAsync();
    Task<IReadOnlyCollection<MemorySearchResult>> SearchAsync(string query);
    Task RebuildIndexAsync();
}

