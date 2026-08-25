using Eling.Application;
using Eling.Core;
using Eling.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace Eling.Mcp.Tests;

public class MemoryToolsTests
{
    private sealed class FakeMemoryService : IMemoryService
    {
        public readonly Dictionary<MemoryId, Memory> Items = new();
        public readonly List<MemorySearchResult> SearchResults = new();
        public string? LastSearchQuery;
        public bool RebuildIndexCalled;

        public Task<Memory> SaveAsync(Memory memory)
        {
            Items[memory.Id] = memory;
            return Task.FromResult(memory);
        }

        public Task<Memory?> GetByIdAsync(MemoryId id) => Task.FromResult(Items.GetValueOrDefault(id));

        public Task<Memory?> UpdateAsync(MemoryId id, string? content = null, MemoryType? type = null, string[]? tags = null, string? source = null, MemoryStatus? status = null)
        {
            if (!Items.TryGetValue(id, out var existing))
            {
                return Task.FromResult<Memory?>(null);
            }

            var updated = new Memory(
                type ?? existing.Type,
                content ?? existing.Content,
                tags ?? existing.Tags.ToArray(),
                source ?? existing.Source,
                status ?? existing.Status,
                existing.Id,
                existing.CreatedAt,
                DateTimeOffset.UtcNow);

            Items[id] = updated;
            return Task.FromResult<Memory?>(updated);
        }

        public Task<bool> DeleteAsync(MemoryId id) => Task.FromResult(Items.Remove(id));

        public Task<IReadOnlyCollection<Memory>> ListAllAsync() =>
            Task.FromResult<IReadOnlyCollection<Memory>>(Items.Values.ToList());

        public Task<IReadOnlyCollection<MemorySearchResult>> SearchAsync(string query)
        {
            LastSearchQuery = query;
            return Task.FromResult<IReadOnlyCollection<MemorySearchResult>>(SearchResults);
        }

        public Task RebuildIndexAsync()
        {
            RebuildIndexCalled = true;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task SaveAsync_WithValidInputs_SavesAndReturnsMemory()
    {
        var service = new FakeMemoryService();
        var tools = new MemoryTools(service);

        var memory = await tools.SaveAsync(
            content: "Architecture decision on MCP",
            type: "decision",
            tags: ["architecture", "mcp"],
            source: "meeting-1");

        Assert.NotNull(memory);
        Assert.Equal("Architecture decision on MCP", memory.Content);
        Assert.Equal(MemoryType.Decision, memory.Type);
        Assert.Equal(["architecture", "mcp"], memory.Tags);
        Assert.Equal("meeting-1", memory.Source);
        Assert.Single(service.Items);
    }

    [Fact]
    public async Task SaveAsync_WithDefaultType_UsesFactType()
    {
        var service = new FakeMemoryService();
        var tools = new MemoryTools(service);

        var memory = await tools.SaveAsync(content: "Just some fact");

        Assert.NotNull(memory);
        Assert.Equal(MemoryType.Fact, memory.Type);
    }

    [Fact]
    public async Task SaveAsync_WithInvalidType_ThrowsArgumentException()
    {
        var service = new FakeMemoryService();
        var tools = new MemoryTools(service);

        await Assert.ThrowsAsync<ArgumentException>(() => tools.SaveAsync("content", type: "invalid-type"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task SaveAsync_WithEmptyContent_ThrowsArgumentException(string? content)
    {
        var service = new FakeMemoryService();
        var tools = new MemoryTools(service);

        await Assert.ThrowsAsync<ArgumentException>(() => tools.SaveAsync(content!));
    }

    [Fact]
    public async Task GetByIdAsync_WhenFound_ReturnsMemory()
    {
        var service = new FakeMemoryService();
        var memory = new Memory(MemoryType.Fact, "Hello world");
        service.Items[memory.Id] = memory;
        var tools = new MemoryTools(service);

        var result = await tools.GetByIdAsync(memory.Id.ToString());

        Assert.NotNull(result);
        Assert.Equal(memory.Id, result.Id);
        Assert.Equal("Hello world", result.Content);
    }

    [Fact]
    public async Task GetByIdAsync_WhenNotFound_ReturnsNull()
    {
        var service = new FakeMemoryService();
        var tools = new MemoryTools(service);
        var missingId = MemoryId.NewId().ToString();

        var result = await tools.GetByIdAsync(missingId);

        Assert.Null(result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task GetByIdAsync_WithEmptyId_ThrowsArgumentException(string? id)
    {
        var service = new FakeMemoryService();
        var tools = new MemoryTools(service);

        await Assert.ThrowsAsync<ArgumentException>(() => tools.GetByIdAsync(id!));
    }

    [Fact]
    public async Task GetByIdAsync_WithInvalidId_ThrowsArgumentException()
    {
        var service = new FakeMemoryService();
        var tools = new MemoryTools(service);

        await Assert.ThrowsAsync<ArgumentException>(() => tools.GetByIdAsync("not-a-valid-ulid"));
    }

    [Fact]
    public async Task DeleteAsync_WhenFound_ReturnsTrueAndRemoves()
    {
        var service = new FakeMemoryService();
        var memory = new Memory(MemoryType.Fact, "To delete");
        service.Items[memory.Id] = memory;
        var tools = new MemoryTools(service);

        var result = await tools.DeleteAsync(memory.Id.ToString());

        Assert.True(result);
        Assert.Empty(service.Items);
    }

    [Fact]
    public async Task DeleteAsync_WhenNotFound_ReturnsFalse()
    {
        var service = new FakeMemoryService();
        var tools = new MemoryTools(service);
        var missingId = MemoryId.NewId().ToString();

        var result = await tools.DeleteAsync(missingId);

        Assert.False(result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task DeleteAsync_WithEmptyId_ThrowsArgumentException(string? id)
    {
        var service = new FakeMemoryService();
        var tools = new MemoryTools(service);

        await Assert.ThrowsAsync<ArgumentException>(() => tools.DeleteAsync(id!));
    }

    [Fact]
    public async Task DeleteAsync_WithInvalidId_ThrowsArgumentException()
    {
        var service = new FakeMemoryService();
        var tools = new MemoryTools(service);

        await Assert.ThrowsAsync<ArgumentException>(() => tools.DeleteAsync("not-a-valid-ulid"));
    }

    [Fact]
    public async Task ListAsync_WithDefaultStatus_FiltersActiveMemories()
    {
        var service = new FakeMemoryService();
        var active = new Memory(MemoryType.Fact, "active", status: MemoryStatus.Active);
        var archived = new Memory(MemoryType.Fact, "archived", status: MemoryStatus.Archived);
        service.Items[active.Id] = active;
        service.Items[archived.Id] = archived;
        var tools = new MemoryTools(service);

        var list = await tools.ListAsync();

        Assert.Single(list);
        Assert.Equal(active.Id, list.First().Id);
    }

    [Fact]
    public async Task ListAsync_WithAll_ReturnsAllMemories()
    {
        var service = new FakeMemoryService();
        var active = new Memory(MemoryType.Fact, "active", status: MemoryStatus.Active);
        var archived = new Memory(MemoryType.Fact, "archived", status: MemoryStatus.Archived);
        service.Items[active.Id] = active;
        service.Items[archived.Id] = archived;
        var tools = new MemoryTools(service);

        var list = await tools.ListAsync("all");

        Assert.Equal(2, list.Count);
    }

    [Fact]
    public async Task ListAsync_WithArchived_ReturnsArchivedOnly()
    {
        var service = new FakeMemoryService();
        var active = new Memory(MemoryType.Fact, "active", status: MemoryStatus.Active);
        var archived = new Memory(MemoryType.Fact, "archived", status: MemoryStatus.Archived);
        service.Items[active.Id] = active;
        service.Items[archived.Id] = archived;
        var tools = new MemoryTools(service);

        var list = await tools.ListAsync("archived");

        Assert.Single(list);
        Assert.Equal(archived.Id, list.First().Id);
    }

    [Fact]
    public async Task ListAsync_WithInvalidStatus_ThrowsArgumentException()
    {
        var service = new FakeMemoryService();
        var tools = new MemoryTools(service);

        await Assert.ThrowsAsync<ArgumentException>(() => tools.ListAsync("unknown-status"));
    }

    [Fact]
    public async Task SearchAsync_WithValidQuery_ReturnsResults()
    {
        var service = new FakeMemoryService();
        var id1 = MemoryId.NewId();
        var id2 = MemoryId.NewId();
        service.SearchResults.Add(new MemorySearchResult(id1, 1.5));
        service.SearchResults.Add(new MemorySearchResult(id2, 0.8));
        var tools = new MemoryTools(service);

        var results = await tools.SearchAsync("architecture");

        Assert.Equal(2, results.Count);
        Assert.Equal("architecture", service.LastSearchQuery);
    }

    [Fact]
    public async Task SearchAsync_WithLimit_LimitsResults()
    {
        var service = new FakeMemoryService();
        for (int i = 0; i < 5; i++)
        {
            service.SearchResults.Add(new MemorySearchResult(MemoryId.NewId(), i + 1));
        }
        var tools = new MemoryTools(service);

        var results = await tools.SearchAsync("query", limit: 2);

        Assert.Equal(2, results.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task SearchAsync_WithEmptyQuery_ThrowsArgumentException(string? query)
    {
        var service = new FakeMemoryService();
        var tools = new MemoryTools(service);

        await Assert.ThrowsAsync<ArgumentException>(() => tools.SearchAsync(query!));
    }

    [Fact]
    public async Task RebuildIndexAsync_InvokesServiceRebuild()
    {
        var service = new FakeMemoryService();
        var tools = new MemoryTools(service);

        await tools.RebuildIndexAsync();

        Assert.True(service.RebuildIndexCalled);
    }

    [Fact]
    public void AddElingMcpServer_ConfiguresCanonicalSourceInstructions()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddElingMcpServerStdio();
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ModelContextProtocol.Server.McpServerOptions>>().Value;

        Assert.NotNull(options.ServerInstructions);
        Assert.Contains(".eling/memories/", options.ServerInstructions);
        Assert.Contains("canonical", options.ServerInstructions, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("add '.eling/' to '.gitignore'", options.ServerInstructions);
    }
}
