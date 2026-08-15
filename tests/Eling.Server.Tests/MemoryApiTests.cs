using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Eling.Server.Tests;

public class MemoryApiTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly HttpClient _client;
    private readonly string _tempDir;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public MemoryApiTests(WebApplicationFactory<Program> factory)
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "eling-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        _client = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.Sources.Clear();
            });
            builder.UseSetting("Environment", "Development");
            builder.UseSetting("Eling:RootPath", _tempDir);
        }).CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task Post_creates_memory_returns_201_with_id()
    {
        var response = await _client.PostAsJsonAsync("/api/memories", new
        {
            content = "Integration test memory"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.True(body.TryGetProperty("id", out var id));
        Assert.False(string.IsNullOrEmpty(id.GetString()));

        // Location header points to the new resource
        Assert.NotNull(response.Headers.Location);
    }

    [Fact]
    public async Task Post_then_GetList_contains_it()
    {
        var postResponse = await _client.PostAsJsonAsync("/api/memories", new
        {
            content = "List test memory",
            type = "Fact"
        });
        Assert.Equal(HttpStatusCode.Created, postResponse.StatusCode);

        var listResponse = await _client.GetAsync("/api/memories");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);

        var memories = await listResponse.Content.ReadFromJsonAsync<JsonElement[]>(JsonOptions);
        Assert.NotNull(memories);
        Assert.Contains(memories, m => m.GetProperty("content").GetString() == "List test memory");
    }

    [Fact]
    public async Task Get_by_id_returns_memory()
    {
        var postResponse = await _client.PostAsJsonAsync("/api/memories", new
        {
            content = "Get by id test"
        });
        var body = await postResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var id = body.GetProperty("id").GetString()!;

        var getResponse = await _client.GetAsync($"/api/memories/{id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        var memory = await getResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("Get by id test", memory.GetProperty("content").GetString());
    }

    [Fact]
    public async Task Get_unknown_id_returns_404()
    {
        // Use a valid ULID format that doesn't exist
        var fakeId = "01h00000000000000000000000";
        var response = await _client.GetAsync($"/api/memories/{fakeId}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Post_with_empty_content_returns_400()
    {
        var response = await _client.PostAsJsonAsync("/api/memories", new
        {
            content = ""
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Delete_returns_204_then_Get_returns_404()
    {
        var postResponse = await _client.PostAsJsonAsync("/api/memories", new
        {
            content = "Delete test memory"
        });
        var body = await postResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var id = body.GetProperty("id").GetString()!;

        var deleteResponse = await _client.DeleteAsync($"/api/memories/{id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var getResponse = await _client.GetAsync($"/api/memories/{id}");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task Search_returns_created_memory()
    {
        await _client.PostAsJsonAsync("/api/memories", new
        {
            content = " searchable purple elephant ",
            type = "Note"
        });

        // Rebuild index so the new memory is searchable
        var rebuildResponse = await _client.PostAsync("/api/memories/rebuild-index", null);
        Assert.Equal(HttpStatusCode.NoContent, rebuildResponse.StatusCode);

        var searchResponse = await _client.GetAsync("/api/memories/search?q=elephant");
        Assert.Equal(HttpStatusCode.OK, searchResponse.StatusCode);

        var results = await searchResponse.Content.ReadFromJsonAsync<JsonElement[]>(JsonOptions);
        Assert.NotNull(results);
        Assert.NotEmpty(results);
    }

    [Fact]
    public async Task Rebuild_index_returns_204()
    {
        var response = await _client.PostAsync("/api/memories/rebuild-index", null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Get_list_filters_by_status()
    {
        // Create a memory (defaults to Active)
        await _client.PostAsJsonAsync("/api/memories", new
        {
            content = "Status filter test"
        });

        // Filter for active
        var activeResponse = await _client.GetAsync("/api/memories?status=active");
        Assert.Equal(HttpStatusCode.OK, activeResponse.StatusCode);

        var activeMemories = await activeResponse.Content.ReadFromJsonAsync<JsonElement[]>(JsonOptions);
        Assert.NotNull(activeMemories);
        Assert.Contains(activeMemories, m => m.GetProperty("content").GetString() == "Status filter test");

        // Filter for archived (should be empty)
        var archivedResponse = await _client.GetAsync("/api/memories?status=archived");
        Assert.Equal(HttpStatusCode.OK, archivedResponse.StatusCode);

        var archivedMemories = await archivedResponse.Content.ReadFromJsonAsync<JsonElement[]>(JsonOptions);
        Assert.NotNull(archivedMemories);
        Assert.DoesNotContain(archivedMemories, m => m.GetProperty("content").GetString() == "Status filter test");
    }

    [Fact]
    public async Task Get_list_invalid_status_returns_400()
    {
        var response = await _client.GetAsync("/api/memories?status=garbage");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_invalid_type_returns_400()
    {
        var response = await _client.PostAsJsonAsync("/api/memories", new
        {
            content = "Bad type test",
            type = "NotAType"
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
