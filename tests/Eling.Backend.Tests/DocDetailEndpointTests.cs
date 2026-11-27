using System.Net;
using System.Text;
using System.Text.Json;
using Eling.Backend.Bootstrap;
using Eling.Core.Docs;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Eling.Backend.Tests;

/// <summary>The dashboard reads a detail by path id: GET /api/docs/{id}.</summary>
[Collection(ElingDataDirCollection.Name)]
public sealed class DocDetailEndpointTests : IAsyncLifetime, IDisposable
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
    public async Task Detail_ByPathId_ReturnsOk()
    {
        var service = _app!.Services.GetRequiredService<DocsService>();
        var root = Path.Combine(Path.GetTempPath(), "eling-doc-detail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var doc = await service.CreateDocumentAsync(root, "docs/specs/detail.md", "# Detail\n\nBody", DocKind.Spec);

        var res = await _client!.GetAsync($"/api/docs/{doc.Id.Value}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task ReadByQuery_WithMalformedId_ReturnsBadRequestEnvelope()
    {
        var res = await _client!.GetAsync("/api/docs/doc?id=not-a-ulid");
        await AssertInvalidIdAsync(res);
    }

    [Fact]
    public async Task Update_WithMalformedId_ReturnsBadRequestEnvelope()
    {
        var res = await _client!.PutAsync("/api/docs/not-a-ulid",
            new StringContent("{\"content\":\"x\"}", Encoding.UTF8, "application/json"));
        await AssertInvalidIdAsync(res);
    }

    [Fact]
    public async Task Delete_WithMalformedId_ReturnsBadRequestEnvelope()
    {
        var res = await _client!.DeleteAsync("/api/docs/not-a-ulid");
        await AssertInvalidIdAsync(res);
    }

    [Fact]
    public async Task Annotate_WithMalformedId_ReturnsBadRequestEnvelope()
    {
        var res = await _client!.PostAsync("/api/docs/not-a-ulid/annotate",
            new StringContent("{\"anchor\":\"a\",\"status\":1}", Encoding.UTF8, "application/json"));
        await AssertInvalidIdAsync(res);
    }

    private static async Task AssertInvalidIdAsync(HttpResponseMessage res)
    {
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        using var payload = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var root = payload.RootElement;
        Assert.Equal("invalid_doc_id", root.GetProperty("code").GetString());
        var message = root.GetProperty("message").GetString() ?? "";
        Assert.True(message.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 6,
            $"Failure message must carry cause and next step: '{message}'");
    }
}
