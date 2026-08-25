using Eling.Core;
using Eling.Application;

namespace Eling.Application;

public class MemoryService : IMemoryService
{
    private readonly IMemoryStorage _storage;
    private readonly IMemoryIndex _index;

    public MemoryService(IMemoryStorage storage, IMemoryIndex index)
    {
        _storage = storage;
        _index = index;
    }

    public async Task<Memory> SaveAsync(Memory memory)
    {
        await _storage.SaveAsync(memory);
        await _index.IndexAsync(memory);
        return memory;
    }

    public Task<Memory?> GetByIdAsync(MemoryId id) => _storage.GetByIdAsync(id);

    public async Task<Memory?> UpdateAsync(MemoryId id, string? content = null, MemoryType? type = null, string[]? tags = null, string? source = null, MemoryStatus? status = null)
    {
        var existing = await _storage.GetByIdAsync(id);
        if (existing is null)
        {
            return null;
        }

        // Type is immutable, so we need to create a new Memory object
        var updatedType = type ?? existing.Type;
        var updatedContent = content ?? existing.Content;
        var updatedTags = tags ?? existing.Tags.ToArray();
        var updatedSource = source ?? existing.Source;
        var updatedStatus = status ?? existing.Status;

        var updated = new Memory(
            updatedType,
            updatedContent,
            updatedTags,
            updatedSource,
            updatedStatus,
            existing.Id,
            existing.CreatedAt,
            DateTimeOffset.UtcNow);

        await _storage.SaveAsync(updated);
        await _index.IndexAsync(updated);
        return updated;
    }

    public async Task<bool> DeleteAsync(MemoryId id)
    {
        if (!await _storage.DeleteAsync(id))
        {
            return false;
        }
        await _index.RemoveAsync(id);
        return true;
    }

    public Task<IReadOnlyCollection<Memory>> ListAllAsync() => _storage.ListAllAsync();

    public Task<IReadOnlyCollection<MemorySearchResult>> SearchAsync(string query) => _index.SearchAsync(query);

    public async Task RebuildIndexAsync()
    {
        var memories = await _storage.ListAllAsync();
        await _index.RebuildAsync(memories);
    }
}
