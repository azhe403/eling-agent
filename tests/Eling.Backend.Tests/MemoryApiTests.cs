using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;

namespace Eling.Backend.Tests;

/// <summary>
/// Integration tests for the memory API. The dual-host architecture means
/// the entry point is no longer a top-level <c>WebApplication.CreateBuilder()</c>
/// call; instead the test fixture builds a self-contained host via
/// <see cref="TestAppBuilder.CreateSelfContained"/> and runs it on a
/// random port. Each test class instance gets a fresh host + data dir so
/// state doesn't leak between tests.
/// </summary>
public class MemoryApiTests : IAsyncLifetime, IDisposable
{
    private readonly string _tempDir;
    private WebApplication? _app;
    private HttpClient? _client;
    private readonly List<HttpClient> _extraClients = [];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public MemoryApiTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "eling-tests-" + Guid.NewGuid().ToString("N")[..8]);
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, ".eling"));

        // Pick a free random port so the test doesn't conflict with dev backend
        // running on 4417/4317.
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        _app = TestAppBuilder.CreateSelfContained(dashboardPort: port);
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        // The dashboard never owns a project .eling; its memory API borrows the
        // data directory of a registered runtime. Register one for this test run.
        var registration = new
        {
            processId = Environment.ProcessId,
            projectRoot = _tempDir,
            dataDirectory = Path.Combine(_tempDir, ".eling"),
            startTime = DateTimeOffset.UtcNow,
            mcpEnabled = false,
            mcpTransport = "none"
        };
        var client = EnsureClient();
        var registerResponse = await client.PostAsJsonAsync("/api/coordinator/register", registration);
        registerResponse.EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync()
    {
        foreach (var c in _extraClients) c.Dispose();
        _extraClients.Clear();

        if (_client is not null)
        {
            _client.Dispose();
            _client = null;
        }

        if (_app is not null)
        {
            try { await _app.StopAsync(); } catch { /* ignore */ }
            try { await _app.DisposeAsync(); } catch { /* ignore */ }
            _app = null;
        }
    }

    public void Dispose()
    {
        TryDeleteDirectory(_tempDir);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Returns the <see cref="HttpClient"/> for the running test host. Throws
    /// if the host failed to start during <see cref="InitializeAsync"/> so
    /// subsequent test methods don't see ambiguous null-forgiving operators.
    /// </summary>
    private HttpClient EnsureClient()
    {
        return _client ?? throw new InvalidOperationException(
            "Test host failed to initialize before the test method ran.");
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
    public async Task PostMemory_ValidContent_Returns201WithIdAndLocation()
    {
        var client = EnsureClient();
        var response = await client.PostAsJsonAsync("/api/memories", new
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
    public async Task PostThenGetList_AfterCreate_ContainsPostedMemory()
    {
        var client = EnsureClient();
        var postResponse = await client.PostAsJsonAsync("/api/memories", new
        {
            content = "List test memory",
            type = "Fact"
        });
        Assert.Equal(HttpStatusCode.Created, postResponse.StatusCode);

        var listResponse = await client.GetAsync("/api/memories");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);

        var memories = await listResponse.Content.ReadFromJsonAsync<JsonElement[]>(JsonOptions);
        Assert.NotNull(memories);
        Assert.Contains(memories, m => m.GetProperty("content").GetString() == "List test memory");
    }

    [Fact]
    public async Task GetById_ExistingMemory_ReturnsMemory()
    {
        var client = EnsureClient();
        var postResponse = await client.PostAsJsonAsync("/api/memories", new
        {
            content = "Get by id test"
        });
        var body = await postResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var id = body.GetProperty("id").GetString();

        var getResponse = await client.GetAsync($"/api/memories/{id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        var memory = await getResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("Get by id test", memory.GetProperty("content").GetString());
    }

    [Fact]
    public async Task GetList_TypeQuery_ReturnsOnlyMatchingType()
    {
        var client = EnsureClient();
        await client.PostAsJsonAsync("/api/memories", new { content = "Fact 1", type = "Fact" });
        await client.PostAsJsonAsync("/api/memories", new { content = "Preference 1", type = "Preference" });

        var factResponse = await client.GetAsync("/api/memories?type=Fact");
        var facts = await factResponse.Content.ReadFromJsonAsync<JsonElement[]>(JsonOptions);
        Assert.All(facts!, f => Assert.Equal("Fact", f.GetProperty("type").GetString()));

        var prefResponse = await client.GetAsync("/api/memories?type=Preference");
        var prefs = await prefResponse.Content.ReadFromJsonAsync<JsonElement[]>(JsonOptions);
        Assert.All(prefs!, p => Assert.Equal("Preference", p.GetProperty("type").GetString()));
    }

    [Fact]
    public async Task GetList_LimitAndOffset_ReturnsPagedResults()
    {
        var client = EnsureClient();
        for (int i = 0; i < 15; i++)
            await client.PostAsJsonAsync("/api/memories", new { content = $"Item {i}", type = "Fact" });

        var page1 = await client.GetAsync("/api/memories?limit=5");
        var items1 = await page1.Content.ReadFromJsonAsync<JsonElement[]>(JsonOptions);
        Assert.Equal(5, items1!.Length);

        var page2 = await client.GetAsync("/api/memories?limit=5&offset=5");
        var items2 = await page2.Content.ReadFromJsonAsync<JsonElement[]>(JsonOptions);
        Assert.Equal(5, items2!.Length);
    }

    [Fact]
    public async Task GetList_InvalidType_Returns400()
    {
        var client = EnsureClient();
        var response = await client.GetAsync("/api/memories?type=InvalidType");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetList_InvalidLimit_Returns400()
    {
        var client = EnsureClient();
        var response = await client.GetAsync("/api/memories?limit=0");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        response = await client.GetAsync("/api/memories?limit=101");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetList_InvalidOffset_Returns400()
    {
        var client = EnsureClient();
        var response = await client.GetAsync("/api/memories?offset=-1");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DeleteMemory_ExistingId_Returns204ThenGetReturns404()
    {
        var client = EnsureClient();
        var postResponse = await client.PostAsJsonAsync("/api/memories", new
        {
            content = "To be deleted"
        });
        var body = await postResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var id = body.GetProperty("id").GetString();

        var deleteResponse = await client.DeleteAsync($"/api/memories/{id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var getResponse = await client.GetAsync($"/api/memories/{id}");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task DeleteMemory_UnknownId_Returns404()
    {
        var client = EnsureClient();
        var response = await client.DeleteAsync("/api/memories/00000000-0000-0000-0000-000000000000");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PatchMemory_ValidPayload_UpdatesContentAndType()
    {
        var client = EnsureClient();
        var postResponse = await client.PostAsJsonAsync("/api/memories", new
        {
            content = "Original content",
            type = "Fact"
        });
        var body = await postResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var id = body.GetProperty("id").GetString();

        var patchResponse = await client.PatchAsJsonAsync($"/api/memories/{id}", new
        {
            content = "Updated content",
            type = "Preference"
        });
        Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);

        var updated = await patchResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("Updated content", updated.GetProperty("content").GetString());
        Assert.Equal("Preference", updated.GetProperty("type").GetString());

        var getResponse = await client.GetAsync($"/api/memories/{id}");
        var memory = await getResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("Updated content", memory.GetProperty("content").GetString());
        Assert.Equal("Preference", memory.GetProperty("type").GetString());
    }

    [Fact]
    public async Task PatchMemory_UnknownId_Returns404()
    {
        var client = EnsureClient();
        var response = await client.PatchAsJsonAsync("/api/memories/00000000-0000-0000-0000-000000000000", new
        {
            content = "Does not matter"
        });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PostNotifyChange_ValidRequest_Returns200()
    {
        var client = EnsureClient();
        var response = await client.PostAsync("/api/coordinator/notify-change", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetEventsStream_Subscribed_ReceivesConnectedThenMutationEvents()
    {
        var client = EnsureClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/events/memories");
        using var sseResponse = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal(HttpStatusCode.OK, sseResponse.StatusCode);
        Assert.Equal("text/event-stream", sseResponse.Content.Headers.ContentType?.MediaType);

        using var stream = await sseResponse.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        // First event is connected handshake
        var line1 = await reader.ReadLineAsync(cts.Token);
        Assert.Equal("data: connected", line1);
        var blank1 = await reader.ReadLineAsync(cts.Token);
        Assert.Equal("", blank1);

        // Trigger a notification in background while reading
        var notifyTask = Task.Run(async () =>
        {
            await Task.Delay(100);
            return await client.PostAsync("/api/coordinator/notify-change", null, cts.Token);
        });

        var line2 = await reader.ReadLineAsync(cts.Token);
        Assert.Equal("data: coordinator", line2);

        var notifyResponse = await notifyTask;
        Assert.Equal(HttpStatusCode.OK, notifyResponse.StatusCode);
    }

    [Fact]
    public async Task GetEventsStream_DashboardAndMCPMutations_ReceivesAllEvents()
    {
        var client = EnsureClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/events/memories");
        using var sseResponse = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal(HttpStatusCode.OK, sseResponse.StatusCode);

        using var stream = await sseResponse.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        // 1. Connection handshake
        var initial = await reader.ReadLineAsync(cts.Token);
        Assert.Equal("data: connected", initial);
        await reader.ReadLineAsync(cts.Token); // blank line

        // 2. Simulate Dashboard Create
        var createResponse = await client.PostAsJsonAsync("/api/memories", new
        {
            content = "Simulated Realtime Memory",
            type = "Note"
        }, cts.Token);
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var id = created.GetProperty("id").GetString();

        var evt1 = await reader.ReadLineAsync(cts.Token);
        Assert.Equal("data: dashboard", evt1);
        await reader.ReadLineAsync(cts.Token); // blank line

        // 3. Simulate MCP stdio mutation (via coordinator notify)
        var mcpNotify = await client.PostAsync("/api/coordinator/notify-change", null, cts.Token);
        mcpNotify.EnsureSuccessStatusCode();

        var evt2 = await reader.ReadLineAsync(cts.Token);
        Assert.Equal("data: coordinator", evt2);
        await reader.ReadLineAsync(cts.Token); // blank line

        // 4. Simulate Dashboard Patch/Edit
        var patchResponse = await client.PatchAsJsonAsync($"/api/memories/{id}", new
        {
            content = "Simulated Updated Realtime Memory"
        }, cts.Token);
        patchResponse.EnsureSuccessStatusCode();

        var evt3 = await reader.ReadLineAsync(cts.Token);
        Assert.Equal("data: dashboard", evt3);
        await reader.ReadLineAsync(cts.Token); // blank line

        // 5. Simulate Dashboard Delete
        var deleteResponse = await client.DeleteAsync($"/api/memories/{id}", cts.Token);
        deleteResponse.EnsureSuccessStatusCode();

        var evt4 = await reader.ReadLineAsync(cts.Token);
        Assert.Equal("data: dashboard", evt4);
    }
}
