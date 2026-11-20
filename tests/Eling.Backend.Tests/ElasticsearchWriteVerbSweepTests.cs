using System.Net;
using Eling.Backend.Endpoints;
using Eling.Core.Audit;
using Eling.Core.Database;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Tests;

/// <summary>
/// No write verb exists under /es/: every unmapped method on the six routes
/// answers 405, not 404 and not a handler. The two POST routes are the only
/// POSTs the surface accepts; everything else is read-only by construction.
/// </summary>
public sealed class ElasticsearchWriteVerbSweepTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "eling-es-verbs-" + Guid.NewGuid().ToString("N"));

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private JsonDatabaseConnectionStore _store = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _store = new JsonDatabaseConnectionStore(Path.Combine(_root, "databases.json"));

        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddLogging();
        builder.Services.AddSingleton<IAuditLogger>(Eling.Core.Audit.NullAuditLogger.Instance);
        builder.Services.AddSingleton<IDatabaseConnectionStore>(_store);
        builder.Services.AddSingleton<IElasticsearchReadService>(new StubElasticsearchReadService());
        builder.WebHost.UseTestServer();
        _app = builder.Build();
        _app.MapDatabaseElasticsearchEndpoints();
        await _app.StartAsync();
        _client = _app.CreateEnvelopeClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        _store.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    public static TheoryData<string> EsPaths => new()
    {
        "/api/databases/any-id/es/indices?includeSystem=false",
        "/api/databases/any-id/es/index?index=logs-app-2026",
        "/api/databases/any-id/es/documents?index=logs-app-2026",
        "/api/databases/any-id/es/histogram?index=logs-app-2026",
        "/api/databases/any-id/es/search",
        "/api/databases/any-id/es/count",
    };

    [Theory]
    [MemberData(nameof(EsPaths))]
    public async Task EsRoutes_RejectPut(string path)
    {
        var response = await SendAsync(HttpMethod.Put, path);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(EsPaths))]
    public async Task EsRoutes_RejectPatch(string path)
    {
        var response = await SendAsync(HttpMethod.Patch, path);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(EsPaths))]
    public async Task EsRoutes_RejectDelete(string path)
    {
        var response = await SendAsync(HttpMethod.Delete, path);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/databases/any-id/es/indices?includeSystem=false")]
    [InlineData("/api/databases/any-id/es/index?index=logs-app-2026")]
    [InlineData("/api/databases/any-id/es/documents?index=logs-app-2026")]
    [InlineData("/api/databases/any-id/es/histogram?index=logs-app-2026")]
    public async Task EsGetOnlyRoutes_RejectPost(string path)
    {
        var response = await SendAsync(HttpMethod.Post, path);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path) =>
        _client.SendAsync(new HttpRequestMessage(method, path) { Content = JsonBody() });

    private static StringContent JsonBody() =>
        new("{}", System.Text.Encoding.UTF8, "application/json");
}
