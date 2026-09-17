using System.Net;
using Eling.Backend.Bootstrap;
using Microsoft.AspNetCore.Builder;

namespace Eling.Backend.Tests;

public class AgentHostBrowseTests : IAsyncLifetime, IDisposable
{
    private WebApplication? _app;
    private HttpClient? _client;
    private string _root = string.Empty;

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "eling-host-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_root, "sub"));

        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        _app = TestAppBuilder.CreateSelfContained(dashboardPort: port);
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    }

    [Fact]
    public async Task Drives_ReturnsAtLeastOne()
    {
        var response = await _client!.GetAsync("/api/agent/host/drives");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.NotEqual("[]", body.Trim());
    }

    [Fact]
    public async Task Browse_ListsSubdirectory()
    {
        var url = $"/api/agent/host/browse?path={Uri.EscapeDataString(_root)}";
        var response = await _client!.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("sub", body);
    }

    [Fact]
    public async Task Browse_MissingDirectory_Returns404()
    {
        var url = $"/api/agent/host/browse?path={Uri.EscapeDataString(Path.Combine(_root, "nope"))}";
        var response = await _client!.GetAsync(url);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
        }
    }

    public void Dispose()
    {
    }
}
