using Eling.Backend.Mcp;
using Eling.Backend.Mcp.Tools;
using Eling.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace Eling.Backend.Tests;

/// <summary>
/// Tests for the canonical MCP memory tool surface. After the
/// Host+Dashboard+Mcp+Application → Core+Backend consolidation, the single
/// MemoryTools class was split into MemoryWriteTool / MemoryReadTool /
/// MemoryIndexTool, with an optional scope parameter (project / global /
/// merged). Tests below exercise each method directly with a fake service
/// so the scope-routing branches stay isolated from the runtime registry.
/// </summary>
public class MemoryToolsTests
{
    private sealed class FakeMemoryService : IMemoryService
    {
        public readonly Dictionary<MemoryId, Memory> Items = new();
        public readonly List<MemorySearchResult> SearchResults = new();
        public string? LastSearchQuery;
        public bool RebuildIndexCalled;

        public Task<SaveResult> SaveAsync(Memory memory)
        {
            var action = Items.ContainsKey(memory.Id) ? SaveAction.Updated : SaveAction.Created;
            Items[memory.Id] = memory;
            return Task.FromResult(new SaveResult(memory, action));
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

    // ---------- MemoryWriteTool.SaveAsync ----------

    [Fact]
    public async Task SaveAsync_WithValidInputs_SavesAndReturnsMemory()
    {
        var service = new FakeMemoryService();
        var tool = new MemoryWriteTool(service);

        var response = await tool.SaveAsync(
            content: "Architecture decision on MCP",
            type: "decision",
            tags: new[] { "architecture", "mcp" },
            source: "meeting-1");

        Assert.NotNull(response);
        Assert.Equal("Architecture decision on MCP", response.Content);
        Assert.Equal(MemoryType.Decision, response.Type);
        Assert.Equal(new[] { "architecture", "mcp" }, response.Tags);
        Assert.Equal("meeting-1", response.Source);
        Assert.Single(service.Items);
    }

    [Fact]
    public async Task SaveAsync_WithDefaultType_UsesFactType()
    {
        var service = new FakeMemoryService();
        var tool = new MemoryWriteTool(service);

        var response = await tool.SaveAsync(content: "Just some fact");

        Assert.NotNull(response);
        Assert.Equal(MemoryType.Fact, response.Type);
    }

    [Fact]
    public async Task SaveAsync_WithInvalidType_ThrowsArgumentException()
    {
        var service = new FakeMemoryService();
        var tool = new MemoryWriteTool(service);

        await Assert.ThrowsAsync<ArgumentException>(() => tool.SaveAsync("content", type: "invalid-type"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task SaveAsync_WithEmptyContent_ThrowsArgumentException(string? content)
    {
        var service = new FakeMemoryService();
        var tool = new MemoryWriteTool(service);

        await Assert.ThrowsAsync<ArgumentException>(() => tool.SaveAsync(content!));
    }

    // ---------- MemoryReadTool.GetByIdAsync ----------

    [Fact]
    public async Task GetByIdAsync_WhenFound_ReturnsMemory()
    {
        var service = new FakeMemoryService();
        var memory = new Memory(MemoryType.Fact, "Hello world");
        service.Items[memory.Id] = memory;
        var tool = new MemoryReadTool(service);

        var result = await tool.GetByIdAsync(memory.Id.ToString());

        Assert.NotNull(result);
        Assert.Equal(memory.Id, result!.Id);
        Assert.Equal("Hello world", result.Content);
    }

    [Fact]
    public async Task GetByIdAsync_WhenNotFound_ReturnsNull()
    {
        var service = new FakeMemoryService();
        var tool = new MemoryReadTool(service);
        var missingId = MemoryId.NewId().ToString();

        var result = await tool.GetByIdAsync(missingId);

        Assert.Null(result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task GetByIdAsync_WithEmptyId_ThrowsArgumentException(string? id)
    {
        var service = new FakeMemoryService();
        var tool = new MemoryReadTool(service);

        await Assert.ThrowsAsync<ArgumentException>(() => tool.GetByIdAsync(id!));
    }

    [Fact]
    public async Task GetByIdAsync_WithInvalidId_ThrowsArgumentException()
    {
        var service = new FakeMemoryService();
        var tool = new MemoryReadTool(service);

        await Assert.ThrowsAsync<ArgumentException>(() => tool.GetByIdAsync("not-a-valid-ulid"));
    }

    // ---------- MemoryWriteTool.DeleteAsync ----------

    [Fact]
    public async Task DeleteAsync_WhenFound_ReturnsTrueAndRemoves()
    {
        var service = new FakeMemoryService();
        var memory = new Memory(MemoryType.Fact, "To delete");
        service.Items[memory.Id] = memory;
        var tool = new MemoryWriteTool(service);

        var result = await tool.DeleteAsync(memory.Id.ToString());

        Assert.True(result);
        Assert.Empty(service.Items);
    }

    [Fact]
    public async Task DeleteAsync_WhenNotFound_ReturnsFalse()
    {
        var service = new FakeMemoryService();
        var tool = new MemoryWriteTool(service);
        var missingId = MemoryId.NewId().ToString();

        var result = await tool.DeleteAsync(missingId);

        Assert.False(result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task DeleteAsync_WithEmptyId_ThrowsArgumentException(string? id)
    {
        var service = new FakeMemoryService();
        var tool = new MemoryWriteTool(service);

        await Assert.ThrowsAsync<ArgumentException>(() => tool.DeleteAsync(id!));
    }

    [Fact]
    public async Task DeleteAsync_WithInvalidId_ThrowsArgumentException()
    {
        var service = new FakeMemoryService();
        var tool = new MemoryWriteTool(service);

        await Assert.ThrowsAsync<ArgumentException>(() => tool.DeleteAsync("not-a-valid-ulid"));
    }

    // ---------- MemoryReadTool.ListAsync ----------

    [Fact]
    public async Task ListAsync_WithActive_FiltersActiveMemories()
    {
        var service = new FakeMemoryService();
        var active = new Memory(MemoryType.Fact, "active", status: MemoryStatus.Active);
        var archived = new Memory(MemoryType.Fact, "archived", status: MemoryStatus.Archived);
        service.Items[active.Id] = active;
        service.Items[archived.Id] = archived;
        var tool = new MemoryReadTool(service);

        var list = await tool.ListAsync(status: "active");

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
        var tool = new MemoryReadTool(service);

        var list = await tool.ListAsync(status: "all");

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
        var tool = new MemoryReadTool(service);

        var list = await tool.ListAsync(status: "archived");

        Assert.Single(list);
        Assert.Equal(archived.Id, list.First().Id);
    }

    [Fact]
    public async Task ListAsync_WithInvalidStatus_ThrowsArgumentException()
    {
        var service = new FakeMemoryService();
        var tool = new MemoryReadTool(service);

        await Assert.ThrowsAsync<ArgumentException>(() => tool.ListAsync(status: "unknown-status"));
    }

    // ---------- MemoryReadTool.SearchAsync ----------

    [Fact]
    public async Task SearchAsync_WithValidQuery_ReturnsResults()
    {
        var service = new FakeMemoryService();
        var id1 = MemoryId.NewId();
        var id2 = MemoryId.NewId();
        service.SearchResults.Add(new MemorySearchResult(id1, 1.5));
        service.SearchResults.Add(new MemorySearchResult(id2, 0.8));
        var tool = new MemoryReadTool(service);

        var results = await tool.SearchAsync("architecture");

        Assert.Equal(2, results.Count);
        Assert.Equal("architecture", service.LastSearchQuery);
    }

    [Fact]
    public async Task SearchAsync_WithLimit_LimitsResults()
    {
        var service = new FakeMemoryService();
        for (var i = 0; i < 5; i++)
        {
            service.SearchResults.Add(new MemorySearchResult(MemoryId.NewId(), i + 1));
        }
        var tool = new MemoryReadTool(service);

        var results = await tool.SearchAsync("query", limit: 2);

        Assert.Equal(2, results.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task SearchAsync_WithEmptyQuery_ThrowsArgumentException(string? query)
    {
        var service = new FakeMemoryService();
        var tool = new MemoryReadTool(service);

        await Assert.ThrowsAsync<ArgumentException>(() => tool.SearchAsync(query!));
    }

    // ---------- MemoryIndexTool.RebuildIndexAsync ----------

    [Fact]
    public async Task RebuildIndexAsync_InvokesServiceRebuild()
    {
        var service = new FakeMemoryService();
        var tool = new MemoryIndexTool(service);

        await tool.RebuildIndexAsync();

        Assert.True(service.RebuildIndexCalled);
    }

    // ---------- Service registration / server instructions ----------

    [Fact]
    public void AddElingMcpServer_ConfiguresCanonicalSourceInstructions()
    {
        var services = new ServiceCollection();
        services.AddElingMcpServerStdio();
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;

        Assert.NotNull(options.ServerInstructions);
        Assert.NotEmpty(ServerInstructions.Sections);
        Assert.Equal(string.Join("\n\n", ServerInstructions.Sections), options.ServerInstructions);
        Assert.Contains(".eling/memories/", options.ServerInstructions);
        Assert.Contains("canonical", options.ServerInstructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".gitignore", options.ServerInstructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("prompt the user for confirmation", options.ServerInstructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Memory Recall Strategy", options.ServerInstructions, StringComparison.OrdinalIgnoreCase);
    }
}