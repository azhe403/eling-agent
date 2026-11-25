using System.Net;
using Eling.Backend.Bootstrap;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace Eling.Backend.Tests;

/// <summary>
/// The dashboard's Rescan button posts an empty body; the endpoint must accept it.
/// </summary>
[Collection(ElingDataDirCollection.Name)]
public sealed class DocReindexEndpointTests : IAsyncLifetime, IDisposable
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
        _client = new HttpClient(new EnvelopeTestHandler { InnerHandler = new HttpClientHandler() })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}")
        };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    public void Dispose() => GC.SuppressFinalize(this);

    [Fact]
    public async Task Reindex_WithoutBody_ReturnsOk()
    {
        var res = await _client!.PostAsync("/api/docs/reindex", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }
}
