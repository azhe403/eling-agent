using System.Net;
using System.Text.Json;
using Eling.Backend.Bootstrap;
using Microsoft.AspNetCore.Builder;

namespace Eling.Backend.Tests;

[Collection(ElingDataDirCollection.Name)]
public class GitIdentityEndpointTests : IAsyncLifetime, IDisposable
{
    private WebApplication? _app;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        _app = TestAppBuilder.CreateSelfContained(dashboardPort: port);
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    }

    [Fact]
    public async Task Identity_Returns200()
    {
        var response = await _client!.GetAsync("/api/system/identity");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Identity_ReturnsANonEmptyName()
    {
        // Asserts shape, not the host's actual git identity: the value depends
        // on the machine running the suite, so pinning it would be an
        // assertion that passes for the wrong reason on someone else's box.
        var response = await _client!.GetAsync("/api/system/identity");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var name = doc.RootElement.GetProperty("name").GetString();

        Assert.False(string.IsNullOrWhiteSpace(name));
    }

    [Fact]
    public async Task Identity_SerializesNameAsCamelCase()
    {
        // The dashboard tolerates either casing; pin the camelCase contract
        // the serializer is configured for so a rename cannot slip through.
        var response = await _client!.GetAsync("/api/system/identity");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.True(doc.RootElement.TryGetProperty("name", out _));
        Assert.True(doc.RootElement.TryGetProperty("email", out _));
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
