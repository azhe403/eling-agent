using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Eling.Backend.Bootstrap;
using Microsoft.AspNetCore.Builder;

namespace Eling.Backend.Tests.Endpoints;

/// <summary>
/// The liveness probe, served at two paths for one reason: the dashboard's
/// dev proxy only forwards the <c>/api</c> prefix to the backend, so a
/// dashboard read of the root path never reaches the API in development.
/// </summary>
[Collection(ElingDataDirCollection.Name)]
public sealed class HealthEndpointTests : IAsyncLifetime, IDisposable
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

    [Theory]
    [InlineData("/health")]
    [InlineData("/api/health")]
    public async Task Health_IsServedAtBothPaths(string path)
    {
        var res = await _client!.GetAsync(path);
        res.EnsureSuccessStatusCode();

        var body = await res.Content.ReadFromJsonAsync<HealthProbe>(JsonOptions);

        Assert.NotNull(body);
        Assert.Equal("Healthy", body!.Status);
        Assert.Equal(Environment.ProcessId, body.Pid);
    }

    [Fact]
    public async Task Health_AtBothPaths_AnswersWithTheSamePayload()
    {
        var root = await _client!.GetAsync("/health");
        var api = await _client!.GetAsync("/api/health");

        root.EnsureSuccessStatusCode();
        api.EnsureSuccessStatusCode();

        var rootBody = await root.Content.ReadFromJsonAsync<HealthProbe>(JsonOptions);
        var apiBody = await api.Content.ReadFromJsonAsync<HealthProbe>(JsonOptions);

        Assert.NotNull(rootBody);
        Assert.NotNull(apiBody);
        Assert.Equal(rootBody!.Pid, apiBody!.Pid);
        Assert.Equal(rootBody.Status, apiBody.Status);
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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed record HealthProbe(
        string Status,
        int Pid);
}
