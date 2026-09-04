using Eling.Core;

namespace Eling.Backend.Tests;

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
    public async Task SaveAsync_ValidMemory_PersistsToStorageAndIndexes()
    {
        var storage = new FakeStorage();
        var index = new FakeIndex();
        var service = new MemoryService(storage, index);
        var memory = NewMemory("hello world");

        var result = await service.SaveAsync(memory);

        Assert.Equal(memory.Id, result.Id);
        Assert.Equal(SaveAction.Created, result.Action);
        Assert.Contains(storage.Items, kv => kv.Value.Id == memory.Id);
        Assert.Contains(index.Indexed, m => m.Id == memory.Id);
    }

    [Fact]
    public async Task GetByIdAsync_ExistingMemory_ReturnsSavedMemory()
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
    public async Task GetByIdAsync_UnknownId_ReturnsNull()
    {
        var service = new MemoryService(new FakeStorage(), new FakeIndex());

        var result = await service.GetByIdAsync(MemoryId.NewId());

        Assert.Null(result);
    }

    [Fact]
    public async Task DeleteAsync_ExistingId_RemovesFromStorageAndIndex()
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
    public async Task DeleteAsync_UnknownId_ReturnsFalseAndSkipsIndex()
    {
        var storage = new FakeStorage();
        var index = new FakeIndex();
        var service = new MemoryService(storage, index);

        var deleted = await service.DeleteAsync(MemoryId.NewId());

        Assert.False(deleted);
        Assert.Empty(index.Removed);
    }

    [Fact]
    public async Task ListAllAsync_MultipleSaved_ReturnsAll()
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
    public async Task SearchAsync_RawQuery_DelegatesAndReturnsUnchangedResults()
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
    public async Task RebuildIndexAsync_StoredMemories_ReadsAndPassesAllToIndex()
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

    [Fact]
    public async Task UpdateAsync_ContentChange_UpdatesAndReturnsMemory()
    {
        var storage = new FakeStorage();
        var index = new FakeIndex();
        var service = new MemoryService(storage, index);
        var memory = NewMemory("original content");
        await service.SaveAsync(memory);

        var result = await service.UpdateAsync(memory.Id, content: "updated content");

        Assert.NotNull(result);
        Assert.Equal("updated content", result!.Content);
        Assert.Equal(memory.Id, result.Id);
        Assert.Equal(memory.CreatedAt, result.CreatedAt);
        Assert.True(result.UpdatedAt > memory.CreatedAt);
    }

    [Fact]
    public async Task UpdateAsync_TypeChange_UpdatesAndReturnsMemory()
    {
        var storage = new FakeStorage();
        var index = new FakeIndex();
        var service = new MemoryService(storage, index);
        var memory = NewMemory("type test");
        await service.SaveAsync(memory);

        var result = await service.UpdateAsync(memory.Id, type: MemoryType.Decision);

        Assert.NotNull(result);
        Assert.Equal(MemoryType.Decision, result!.Type);
    }

    [Fact]
    public async Task UpdateAsync_TagsChange_UpdatesAndReturnsMemory()
    {
        var storage = new FakeStorage();
        var index = new FakeIndex();
        var service = new MemoryService(storage, index);
        var memory = NewMemory("tags test");
        await service.SaveAsync(memory);

        var result = await service.UpdateAsync(memory.Id, tags: new[] { "new-tag", "another-tag" });

        Assert.NotNull(result);
        Assert.Equal(2, result!.Tags.Count);
        Assert.Contains("new-tag", result.Tags);
        Assert.Contains("another-tag", result.Tags);
    }

    [Fact]
    public async Task UpdateAsync_StatusChange_UpdatesAndReturnsMemory()
    {
        var storage = new FakeStorage();
        var index = new FakeIndex();
        var service = new MemoryService(storage, index);
        var memory = NewMemory("status test");
        await service.SaveAsync(memory);

        var result = await service.UpdateAsync(memory.Id, status: MemoryStatus.Archived);

        Assert.NotNull(result);
        Assert.Equal(MemoryStatus.Archived, result!.Status);
    }

    [Fact]
    public async Task UpdateAsync_UnknownId_ReturnsNull()
    {
        var service = new MemoryService(new FakeStorage(), new FakeIndex());

        var result = await service.UpdateAsync(MemoryId.NewId(), content: "nope");

        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateAsync_PartialUpdate_PreservesUnchangedFields()
    {
        var storage = new FakeStorage();
        var index = new FakeIndex();
        var service = new MemoryService(storage, index);
        var memory = new Memory(MemoryType.Fact, "original", new[] { "tag1" }, "source1");
        await service.SaveAsync(memory);

        var result = await service.UpdateAsync(memory.Id, content: "only content changed");

        Assert.NotNull(result);
        Assert.Equal("only content changed", result!.Content);
        Assert.Equal(MemoryType.Fact, result.Type);
        Assert.Single(result.Tags);
        Assert.Equal("source1", result.Source);
    }

    [Fact]
    public async Task UpdateAsync_AfterUpdate_IndexesUpdatedMemory()
    {
        var storage = new FakeStorage();
        var index = new FakeIndex();
        var service = new MemoryService(storage, index);
        var memory = NewMemory("index test");
        await service.SaveAsync(memory);

        await service.UpdateAsync(memory.Id, content: "indexed update");

        Assert.Equal(2, index.Indexed.Count);
        Assert.Equal("indexed update", index.Indexed[1].Content);
    }

    [Fact]
    public async Task SaveAsync_ContentMatchesActiveMemory_UpdatesInPlace()
    {
        var storage = new FakeStorage();
        var index = new FakeIndex();
        var service = new MemoryService(storage, index);
        var original = new Memory(MemoryType.Fact, "Remember the token", new[] { "tag1" }, "source1");
        var firstResult = await service.SaveAsync(original);
        Assert.Equal(SaveAction.Created, firstResult.Action);

        // Same content, different casing/whitespace and extra tags.
        var incoming = new Memory(MemoryType.Fact, "  REMEMBER THE TOKEN  ", new[] { "tag2" }, "source2");
        var result = await service.SaveAsync(incoming);

        // No duplicate entry: exactly one memory remains.
        Assert.Single(storage.Items);
        Assert.Equal(SaveAction.Updated, result.Action);
        Assert.Equal(original.Id, result.Id);
        // Preserved original identity/timestamps, refreshed update time.
        Assert.Equal(original.CreatedAt, result.CreatedAt);
        Assert.True(result.UpdatedAt >= original.CreatedAt);
        // Tags merged, source preferred from the incoming save.
        Assert.Equal(new[] { "tag1", "tag2" }, result.Tags.OrderBy(t => t).ToArray());
        Assert.Equal("source2", result.Source);
        Assert.Equal(2, index.Indexed.Count); // original + merge
    }

    [Fact]
    public async Task SaveAsync_ContentDiffers_InsertsNew()
    {
        var storage = new FakeStorage();
        var index = new FakeIndex();
        var service = new MemoryService(storage, index);
        var r1 = await service.SaveAsync(NewMemory("hello world"));
        var r2 = await service.SaveAsync(NewMemory("completely different"));

        Assert.Equal(SaveAction.Created, r1.Action);
        Assert.Equal(SaveAction.Created, r2.Action);
        Assert.Equal(2, storage.Items.Count);
    }

    [Fact]
    public async Task SaveAsync_MatchingContentArchived_InsertsNew()
    {
        var storage = new FakeStorage();
        var index = new FakeIndex();
        var service = new MemoryService(storage, index);
        var archived = new Memory(MemoryType.Fact, "duplicate content", status: MemoryStatus.Archived);
        await service.SaveAsync(archived);

        var incoming = new Memory(MemoryType.Fact, "duplicate content");
        var result = await service.SaveAsync(incoming);

        Assert.Equal(SaveAction.Created, result.Action);
        Assert.Equal(2, storage.Items.Count);
        Assert.NotEqual(archived.Id, result.Id);
    }

    [Fact]
    public async Task SaveAsync_InactiveOrArchivedTarget_DoesNotMatchForDedup()
    {
        var storage = new FakeStorage();
        var index = new FakeIndex();
        var service = new MemoryService(storage, index);
        var superseded = new Memory(MemoryType.Fact, "superseded content", status: MemoryStatus.Superseded);
        await service.SaveAsync(superseded);

        var result = await service.SaveAsync(NewMemory("superseded content"));

        Assert.Equal(SaveAction.Created, result.Action);
        Assert.Equal(2, storage.Items.Count);
        Assert.NotEqual(superseded.Id, result.Id);
    }
}
