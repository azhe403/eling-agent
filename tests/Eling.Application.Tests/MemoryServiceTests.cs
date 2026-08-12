using Eling.Application;
using Eling.Core;
using Eling.Index;
using Eling.Storage;

namespace Eling.Application.Tests;

public class MemoryServiceTests
{
    private sealed class FakeStorage : IMemoryStorage
    {
        public readonly Dictionary<MemoryId, Memory> Items = new();

        public Task SaveAsync(Memory memory)
        {
            Items[memory.Id] = memory;
            return Task.CompletedTask;
        }

        public Task<Memory?> GetByIdAsync(MemoryId id) => Task.FromResult(Items.GetValueOrDefault(id));

        public Task<bool> DeleteAsync(MemoryId id) => Task.FromResult(Items.Remove(id));

        public Task<IReadOnlyCollection<Memory>> ListAllAsync() =>
            Task.FromResult<IReadOnlyCollection<Memory>>(Items.Values.ToList());
    }

    private sealed class FakeIndex : IMemoryIndex
    {
        public readonly List<Memory> Indexed = new();
        public readonly List<MemoryId> Removed = new();
        public IEnumerable<Memory>? LastRebuildBatch;
        public string? LastSearchQuery;
        public IReadOnlyCollection<MemorySearchResult> SearchResults = Array.Empty<MemorySearchResult>();

        public Task IndexAsync(Memory memory)
        {
            Indexed.Add(memory);
            return Task.CompletedTask;
        }

        public Task RemoveAsync(MemoryId id)
        {
            Removed.Add(id);
            return Task.CompletedTask;
        }

        public Task RebuildAsync(IEnumerable<Memory> memories)
        {
            LastRebuildBatch = memories.ToList();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyCollection<MemorySearchResult>> SearchAsync(string query)
        {
            LastSearchQuery = query;
            return Task.FromResult(SearchResults);
        }
    }

    private static Memory NewMemory(string content) =>
        new(MemoryType.Fact, content);

    [Fact]
    public async Task SaveAsync_persists_to_storage_and_indexes()
    {
        var storage = new FakeStorage();
        var index = new FakeIndex();
        var service = new MemoryService(storage, index);
        var memory = NewMemory("hello world");

        var result = await service.SaveAsync(memory);

        Assert.Same(memory, result);
        Assert.Contains(storage.Items, kv => kv.Value.Id == memory.Id);
        Assert.Contains(index.Indexed, m => m.Id == memory.Id);
    }

    [Fact]
    public async Task GetByIdAsync_returns_saved_memory()
    {
        var storage = new FakeStorage();
        var index = new FakeIndex();
        var service = new MemoryService(storage, index);
        var memory = NewMemory("get me");
        await service.SaveAsync(memory);

        var result = await service.GetByIdAsync(memory.Id);

        Assert.NotNull(result);
        Assert.Equal(memory.Content, result!.Content);
    }

    [Fact]
    public async Task GetByIdAsync_returns_null_for_unknown_id()
    {
        var service = new MemoryService(new FakeStorage(), new FakeIndex());

        var result = await service.GetByIdAsync(MemoryId.NewId());

        Assert.Null(result);
    }

    [Fact]
    public async Task DeleteAsync_removes_from_storage_and_index_when_present()
    {
        var storage = new FakeStorage();
        var index = new FakeIndex();
        var service = new MemoryService(storage, index);
        var memory = NewMemory("delete me");
        await service.SaveAsync(memory);

        var deleted = await service.DeleteAsync(memory.Id);

        Assert.True(deleted);
        Assert.Empty(storage.Items);
        Assert.Contains(index.Removed, id => id == memory.Id);
    }

    [Fact]
    public async Task DeleteAsync_returns_false_and_does_not_touch_index_when_absent()
    {
        var storage = new FakeStorage();
        var index = new FakeIndex();
        var service = new MemoryService(storage, index);

        var deleted = await service.DeleteAsync(MemoryId.NewId());

        Assert.False(deleted);
        Assert.Empty(index.Removed);
    }

    [Fact]
    public async Task ListAllAsync_returns_all_saved_memories()
    {
        var storage = new FakeStorage();
        var index = new FakeIndex();
        var service = new MemoryService(storage, index);
        var first = NewMemory("first");
        var second = NewMemory("second");
        await service.SaveAsync(first);
        await service.SaveAsync(second);

        var result = await service.ListAllAsync();

        Assert.Equal(2, result.Count);
        Assert.Contains(result, m => m.Content == "first");
        Assert.Contains(result, m => m.Content == "second");
    }

    [Fact]
    public async Task SearchAsync_delegates_raw_query_to_index_and_returns_results_unchanged()
    {
        var storage = new FakeStorage();
        var index = new FakeIndex();
        var service = new MemoryService(storage, index);
        var expected = new List<MemorySearchResult> { new(MemoryId.NewId(), 0.75) };
        index.SearchResults = expected;

        var result = await service.SearchAsync("important fact");

        Assert.Equal("important fact", index.LastSearchQuery);
        Assert.Same(expected, result);
    }

    [Fact]
    public async Task RebuildIndexAsync_reads_all_memories_from_storage_and_passes_to_index()
    {
        var storage = new FakeStorage();
        var index = new FakeIndex();
        var service = new MemoryService(storage, index);
        var first = NewMemory("rebuild 1");
        var second = NewMemory("rebuild 2");
        await storage.SaveAsync(first);
        await storage.SaveAsync(second);

        await service.RebuildIndexAsync();

        Assert.NotNull(index.LastRebuildBatch);
        Assert.Equal(2, index.LastRebuildBatch!.Count());
        Assert.Contains(index.LastRebuildBatch!, m => m.Content == "rebuild 1");
        Assert.Contains(index.LastRebuildBatch!, m => m.Content == "rebuild 2");
    }
}
