using Eling.Core;
using Eling.Index;
using Eling.Storage;

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
