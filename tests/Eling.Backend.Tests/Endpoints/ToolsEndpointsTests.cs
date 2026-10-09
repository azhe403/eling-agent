using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Eling.Backend.Bootstrap;
using Microsoft.AspNetCore.Builder;

namespace Eling.Backend.Tests.Endpoints;

[Collection(ElingDataDirCollection.Name)]
public sealed class ToolsEndpointsTests : IAsyncLifetime, IDisposable
{
    private WebApplication? _app;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        _app = TestAppBuilder.CreateSelfContained(dashboardPort: port);
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    }

    [Fact]
    public async Task GetTools_ReturnsCatalogWithStatusFlags()
    {
        var client = _client!;

        var res = await client.GetAsync("/api/tools");
        res.EnsureSuccessStatusCode();

        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("memory_recall", body);
        Assert.Contains("file_delete", body);
        Assert.Contains("codebase_search", body);
        Assert.Contains("enabled", body);
        Assert.Contains("isProtected", body);
    }

    [Fact]
    public async Task PutTools_DisableSingleTool_TakesEffectOnNextGet()
    {
        var client = _client!;

        var put = await client.PutAsJsonAsync("/api/tools", new
        {
            toolName = "file_delete",
            enabled = false,
        });
        put.EnsureSuccessStatusCode();

        var get = await client.GetAsync("/api/tools");
        get.EnsureSuccessStatusCode();
        var body = await get.Content.ReadAsStringAsync();

        Assert.Contains("\"name\":\"file_delete\"", body);
        Assert.Contains("\"name\":\"file_delete\",\"group\":\"filesystem\"", body);

        var reEnable = await client.PutAsJsonAsync("/api/tools", new
        {
            toolName = "file_delete",
            enabled = true,
        });
        reEnable.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task PutTools_DisableProtectedTool_ReturnsBadRequest()
    {
        var client = _client!;

        var res = await client.PutAsJsonAsync("/api/tools", new
        {
            toolName = "memory_recall",
            enabled = false,
        });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task PutTools_UnknownTool_ReturnsBadRequest()
    {
        var client = _client!;

        var res = await client.PutAsJsonAsync("/api/tools", new
        {
            toolName = "does_not_exist",
            enabled = false,
        });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task PutTools_DisableGroup_DisablesEveryMember()
    {
        var client = _client!;

        var put = await client.PutAsJsonAsync("/api/tools", new
        {
            group = "codebase",
            enabled = false,
        });
        put.EnsureSuccessStatusCode();

        var get = await client.GetAsync("/api/tools");
        get.EnsureSuccessStatusCode();
        var items = await get.Content.ReadFromJsonAsync<List<ToolItemProbe>>();

        Assert.NotNull(items);
        var codebase = items!.Where(item => item.Group == "codebase").ToList();
        Assert.NotEmpty(codebase);
        Assert.All(codebase, item => Assert.False(item.Enabled));

        var restore = await client.PutAsJsonAsync("/api/tools", new
        {
            group = "codebase",
            enabled = true,
        });
        restore.EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        _client?.Dispose();
    }

    public void Dispose()
    {
        _client?.Dispose();
    }

    private sealed record ToolItemProbe(
        string Name,
        string Group,
        bool Enabled);
}
