using System.Net;
using System.Net.Http.Json;
using Eling.Backend.Bootstrap;
using Microsoft.AspNetCore.Builder;

namespace Eling.Backend.Tests;

public class AgentWorkspaceTests : IAsyncLifetime, IDisposable
{
    private WebApplication? _app;
    private HttpClient? _client;
    private string _root = string.Empty;

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "eling-agent-ws-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);

        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        _app = TestAppBuilder.CreateSelfContained(dashboardPort: port);
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        var attach = await _client.PostAsJsonAsync("/api/agent/workspaces/", new { path = _root });
        attach.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task ListWorkspaces_ShowsAttachedRoot()
    {
        var response = await _client!.GetAsync("/api/agent/workspaces/");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("roots", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadOutsideRoot_Returns403()
    {
        var url = $"/api/agent/files/read?root={Uri.EscapeDataString(_root)}&path={Uri.EscapeDataString("../..")}";
        var response = await _client!.GetAsync(url);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ReadBinaryFile_Returns415()
    {
        var path = Path.Combine(_root, "bin.dat");
        File.WriteAllBytes(path, [0x41, 0x00, 0x42]);

        var url = $"/api/agent/files/read?root={Uri.EscapeDataString(_root)}&path=bin.dat";
        var response = await _client!.GetAsync(url);
        Assert.Equal((HttpStatusCode)415, response.StatusCode);
    }

    [Fact]
    public async Task WriteThenRead_RoundTrips()
    {
        var content = new string('a', 100 * 1024);
        var write = await _client!.PostAsJsonAsync("/api/agent/files/write", new
        {
            root = _root,
            path = "big.txt",
            content
        });
        write.EnsureSuccessStatusCode();

        var url = $"/api/agent/files/read?root={Uri.EscapeDataString(_root)}&path=big.txt";
        var response = await _client!.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(new string('a', 64), body);
    }

    [Fact]
    public async Task OversizeWrite_Returns400()
    {
        var content = new string('b', 200 * 1024);
        var write = await _client!.PostAsJsonAsync("/api/agent/files/write", new
        {
            root = _root,
            path = "huge.txt",
            content
        });
        Assert.Equal(HttpStatusCode.BadRequest, write.StatusCode);
    }

    [Fact]
    public async Task Detach_RemovesFromListButKeepsDirectory()
    {
        var delete = await _client!.DeleteAsync($"/api/agent/workspaces/?path={Uri.EscapeDataString(_root)}");
        delete.EnsureSuccessStatusCode();
        var body = await delete.Content.ReadAsStringAsync();
        Assert.DoesNotContain("eling-agent-ws-", body);
        Assert.True(Directory.Exists(_root));
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
