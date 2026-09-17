using System.Net.Http.Json;
using Eling.Backend.Bootstrap;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Eling.Backend.Tests;

public class AgentProviderTests : IAsyncLifetime, IDisposable
{
    private WebApplication? _app;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        _app = TestAppBuilder.CreateSelfContained(dashboardPort: port);
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    }

    [Fact]
    public async Task PutThenGet_RoundTripsWithoutLeakingKey()
    {
        var client = _client!;
        var put = await client.PutAsJsonAsync("/api/agent/provider/", new
        {
            baseUrl = "http://127.0.0.1:11434/v1",
            model = "qwen3",
            apiKey = "secret-1"
        });
        put.EnsureSuccessStatusCode();

        var get = await client.GetAsync("/api/agent/provider/");
        get.EnsureSuccessStatusCode();
        var body = await get.Content.ReadAsStringAsync();
        Assert.Contains("hasKey", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-1", body);
    }

    [Fact]
    public async Task EmptyApiKey_KeepsStoredKey()
    {
        var client = _client!;
        var first = await client.PutAsJsonAsync("/api/agent/provider/", new
        {
            baseUrl = "http://127.0.0.1:11434/v1",
            model = "qwen3",
            apiKey = "secret-2"
        });
        first.EnsureSuccessStatusCode();

        var second = await client.PutAsJsonAsync("/api/agent/provider/", new
        {
            baseUrl = "http://127.0.0.1:11434/v1",
            model = "qwen3",
            apiKey = ""
        });
        second.EnsureSuccessStatusCode();

        var get = await client.GetAsync("/api/agent/provider/");
        get.EnsureSuccessStatusCode();
        var body = await get.Content.ReadAsStringAsync();
        Assert.Contains("\"hasKey\":true", body);
    }

    [Fact]
    public async Task ModelsEndpoint_ReturnsStubIds()
    {
        var stubPort = await StartStubProviderAsync();
        var client = _client!;

        var put = await client.PutAsJsonAsync("/api/agent/provider/", new
        {
            baseUrl = $"http://127.0.0.1:{stubPort}",
            model = "m1",
            apiKey = ""
        });
        put.EnsureSuccessStatusCode();

        var response = await client.PostAsync("/api/agent/provider/models", null);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("m1", body);
        Assert.Contains("m2", body);

        var cached = await client.GetAsync("/api/agent/provider/");
        cached.EnsureSuccessStatusCode();
        var cachedBody = await cached.Content.ReadAsStringAsync();
        Assert.Contains("m1", cachedBody);
        Assert.Contains("m2", cachedBody);
    }

    [Fact]
    public async Task TestEndpoint_ReturnsOkAgainstStub()
    {
        var stubPort = await StartStubProviderAsync();
        var client = _client!;

        var put = await client.PutAsJsonAsync("/api/agent/provider/", new
        {
            baseUrl = $"http://127.0.0.1:{stubPort}",
            model = "m1",
            apiKey = ""
        });
        put.EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync("/api/agent/provider/test", new { });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"ok\":true", body);
    }

    private static readonly List<WebApplication> _stubs = [];

    private static async Task<int> StartStubProviderAsync()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var stub = builder.Build();
        stub.MapGet("/models", () => Results.Ok(new { data = new[] { new { id = "m1" }, new { id = "m2" } } }));
        stub.MapPost("/chat/completions", () => Results.Ok(new { choices = new[] { new { message = new { content = "pong" } } } }));
        await stub.StartAsync();
        _stubs.Add(stub);
        return port;
    }

    public async Task DisposeAsync()
    {
        if (_client is not null)
        {
            _client.Dispose();
            _client = null;
        }

        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
        }
    }

    public void Dispose()
    {
    }
}
