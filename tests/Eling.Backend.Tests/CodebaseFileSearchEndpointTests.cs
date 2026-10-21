using System.Net;
using System.Text.Json;
using Eling.Backend.Bootstrap;
using Eling.Backend.Codebase;
using Eling.Backend.Endpoints;
using Eling.Core.Codebase;
using Eling.Core.Memory;
using Eling.Core.Scope;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eling.Backend.Tests;

/// <summary>
/// The search-scope selector on /api/codebase/search, over real HTTP: the
/// path and file scopes answer with file rows, the content scope keeps
/// answering with code snippets, and an unknown scope is rejected instead of
/// silently searching something else.
/// </summary>
[Collection(ElingDataDirCollection.Name)]
public sealed class CodebaseFileSearchEndpointTests : IAsyncLifetime
{
    private const string DataDirEnv = "ELING_DATA_DIR";

    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(), "eling-filesearch-ep-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _dataDir = Path.Combine(
        Path.GetTempPath(), "eling-filesearch-ep-data-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string? _originalDataDir = Environment.GetEnvironmentVariable(DataDirEnv);

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private RuntimeRegistry _registry = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_workspace, "src", "backend"));
        Directory.CreateDirectory(_dataDir);
        Environment.SetEnvironmentVariable(DataDirEnv, _dataDir);

        await File.WriteAllTextAsync(
            Path.Combine(_workspace, "src", "backend", "Widget.cs"),
            "public sealed class ZephyrMarkerWidget { }\n");

        var local = new CodebaseIndexService(
            _workspace,
            new SqliteCodebaseIndex(ElingPaths.ResolveCodebaseDbPath(_workspace)));
        await local.IndexAsync(full: true);

        var registry = new RuntimeRegistry(
            NullLogger<RuntimeRegistry>.Instance,
            UserScope.Resolve(Path.Combine(_dataDir, "user-scope")));
        _registry = registry;

        var broadcaster = new CodebaseRebuildBroadcaster();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(local);
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton(broadcaster);
        builder.Services.AddSingleton(new MemoryChangeBroadcaster());
        builder.Services.AddSingleton<IMemoryChangeNotifier>(NullMemoryChangeNotifier.Instance);
        builder.Services.AddSingleton<CodebaseWatcherService>();
        builder.Services.AddSingleton<Eling.Backend.Services.ProcessMetricsTracker>();
        builder.Services.AddSingleton<Eling.Backend.Services.RuntimeMetricsSampler>();
        builder.Services.AddSingleton(sp => new Eling.Backend.Services.RuntimeMetricsBroadcaster(
            sp.GetRequiredService<Eling.Backend.Services.RuntimeMetricsSampler>(),
            sp.GetRequiredService<ILogger<Eling.Backend.Services.RuntimeMetricsBroadcaster>>()));
        // The rebuild routes are mapped with the same group, so their handler
        // parameters must be resolvable even though this test never calls
        // them — an unresolvable one is inferred as a request body, and a
        // GET endpoint refuses an inferred body.
        builder.Services.AddSingleton<CodebaseRebuildJobRunner>(sp =>
            new CodebaseRebuildJobRunner(
                local,
                broadcaster,
                NullMemoryChangeNotifier.Instance,
                NullLogger<CodebaseRebuildJobRunner>.Instance,
                sp.GetService<IHostApplicationLifetime>(),
                reindex: (root, full, _) =>
                    Task.FromResult(new CodebaseIndexResult(2, 5, 1, 0, "stub.db"))));

        _app = builder.Build();
        _app.MapCodebaseRoutes();
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    [Fact]
    public async Task ThePathScopeAnswersWithFileRows()
    {
        var (status, body) = await GetAsync("q=backend&search=path");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("files", body.RootElement.GetProperty("mode").GetString());
        var result = Assert.Single(body.RootElement.GetProperty("results").EnumerateArray());
        Assert.Equal("src/backend/Widget.cs", result.GetProperty("path").GetString());
        Assert.True(result.GetProperty("chunkCount").GetInt32() > 0);
    }

    [Fact]
    public async Task TheFileScopeIgnoresFolderNames()
    {
        var (status, body) = await GetAsync("q=backend&search=file");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("files", body.RootElement.GetProperty("mode").GetString());
        Assert.Empty(body.RootElement.GetProperty("results").EnumerateArray());
    }

    [Fact]
    public async Task TheFileScopeFindsAFileByItsName()
    {
        var (status, body) = await GetAsync("q=widget&search=file");

        Assert.Equal(HttpStatusCode.OK, status);
        var result = Assert.Single(body.RootElement.GetProperty("results").EnumerateArray());
        Assert.Equal("src/backend/Widget.cs", result.GetProperty("path").GetString());
    }

    [Fact]
    public async Task WithoutTheScopeParameterTheSearchStillAnswersWithCodeSnippets()
    {
        var (status, body) = await GetAsync("q=ZephyrMarkerWidget");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("search", body.RootElement.GetProperty("mode").GetString());
        var result = Assert.Single(body.RootElement.GetProperty("results").EnumerateArray());
        Assert.Equal("src/backend/Widget.cs", result.GetProperty("path").GetString());
        Assert.True(result.GetProperty("startLine").GetInt32() > 0);
    }

    [Fact]
    public async Task AnUnknownScopeIsRejected()
    {
        var response = await _client.GetAsync("/api/codebase/search?q=widget&search=elsewhere");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("content, path, file", body, StringComparison.Ordinal);
    }

    private async Task<(HttpStatusCode Status, JsonDocument Body)> GetAsync(string query)
    {
        var response = await _client.GetAsync("/api/codebase/search?" + query);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (response.StatusCode, body);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        _registry.Dispose();
        if (_originalDataDir is null) Environment.SetEnvironmentVariable(DataDirEnv, null);
        else Environment.SetEnvironmentVariable(DataDirEnv, _originalDataDir);
        CodebaseRebuildScopeTests.TryDelete(_workspace);
        CodebaseRebuildScopeTests.TryDelete(_dataDir);
    }
}
