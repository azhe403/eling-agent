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
            builder.UseSetting("Eling:EnableMcp", "false");
        }).CreateClient();
    }

public void Dispose()
    {
        _client.Dispose();

        TryDeleteDirectory(_tempDir);
    }

    private static void TryDeleteDirectory(string path, int retries = 5, int delayMs = 200)
    {
        if (!Directory.Exists(path)) return;

        for (int i = 0; i < retries; i++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(delayMs);
            }
        }
    }

    private static void TryDeleteFile(string path, int retries = 5, int delayMs = 200)
    {
        if (!File.Exists(path)) return;

        for (int i = 0; i < retries; i++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(delayMs);
            }
        }
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
        var id = body.GetProperty("id").GetString();

        var getResponse = await _client.GetAsync($"/api/memories/{id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        var memory = await getResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("Get by id test", memory.GetProperty("content").GetString());
    }

    [Fact]
    public async Task Get_list_supports_type_filter()
    {
        await _client.PostAsJsonAsync("/api/memories", new { content = "Fact 1", type = "Fact" });
        await _client.PostAsJsonAsync("/api/memories", new { content = "Preference 1", type = "Preference" });

        var factResponse = await _client.GetAsync("/api/memories?type=Fact");
        var facts = await factResponse.Content.ReadFromJsonAsync<JsonElement[]>(JsonOptions);
        Assert.All(facts!, f => Assert.Equal("Fact", f.GetProperty("type").GetString()));

        var prefResponse = await _client.GetAsync("/api/memories?type=Preference");
        var prefs = await prefResponse.Content.ReadFromJsonAsync<JsonElement[]>(JsonOptions);
        Assert.All(prefs!, p => Assert.Equal("Preference", p.GetProperty("type").GetString()));
    }

    [Fact]
    public async Task Get_list_supports_pagination()
    {
        for (int i = 0; i < 15; i++)
            await _client.PostAsJsonAsync("/api/memories", new { content = $"Item {i}", type = "Fact" });

        var page1 = await _client.GetAsync("/api/memories?limit=5");
        var items1 = await page1.Content.ReadFromJsonAsync<JsonElement[]>(JsonOptions);
        Assert.Equal(5, items1!.Length);

        var page2 = await _client.GetAsync("/api/memories?limit=5&offset=5");
        var items2 = await page2.Content.ReadFromJsonAsync<JsonElement[]>(JsonOptions);
        Assert.Equal(5, items2!.Length);
    }

    [Fact]
    public async Task Get_list_invalid_status_returns_400()
    {
        var response = await _client.GetAsync("/api/memories?type=InvalidType");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Get_list_invalid_limit_returns_400()
    {
        var response = await _client.GetAsync("/api/memories?limit=0");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        response = await _client.GetAsync("/api/memories?limit=101");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Get_list_invalid_offset_returns_400()
    {
        var response = await _client.GetAsync("/api/memories?offset=-1");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Delete_returns_204_then_Get_returns_404()
    {
        var postResponse = await _client.PostAsJsonAsync("/api/memories", new
        {
            content = "To be deleted"
        });
        var body = await postResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var id = body.GetProperty("id").GetString();

        var deleteResponse = await _client.DeleteAsync($"/api/memories/{id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var getResponse = await _client.GetAsync($"/api/memories/{id}");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task Delete_nonexistent_returns_404()
    {
        var response = await _client.DeleteAsync("/api/memories/00000000-0000-0000-0000-000000000000");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Patch_updates_content_and_type()
    {
        var postResponse = await _client.PostAsJsonAsync("/api/memories", new
        {
            content = "Original content",
            type = "Fact"
        });
        var body = await postResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var id = body.GetProperty("id").GetString();

        var patchResponse = await _client.PatchAsJsonAsync($"/api/memories/{id}", new
        {
            content = "Updated content",
            type = "Preference"
        });
        Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);

        var updated = await patchResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("Updated content", updated.GetProperty("content").GetString());
        Assert.Equal("Preference", updated.GetProperty("type").GetString());

        var getResponse = await _client.GetAsync($"/api/memories/{id}");
        var memory = await getResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("Updated content", memory.GetProperty("content").GetString());
        Assert.Equal("Preference", memory.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Patch_nonexistent_returns_404()
    {
        var response = await _client.PatchAsJsonAsync("/api/memories/00000000-0000-0000-0000-000000000000", new
        {
            content = "Does not matter"
        });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}