using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Eling.Backend.Codebase;
using Eling.Backend.Endpoints;
using Eling.Core.Codebase;
using Eling.Core.Memory;
using Eling.Core.Projects;
using Eling.Core.Scope;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eling.Backend.Tests;

[Collection(ElingDataDirCollection.Name)]
public sealed class CodebaseSearchStatsTests : IAsyncLifetime
{
    private const string DataDirEnv = "ELING_DATA_DIR";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private string _workspace = null!;
    private string _dataDir = null!;
    private string? _originalDataDir;
    private HttpClient _client = null!;
    private RuntimeRegistry _registry = null!;

    public async Task InitializeAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        _workspace = Path.Combine(Path.GetTempPath(), "eling-searchstats-ws-" + tag);
        _dataDir = Path.Combine(Path.GetTempPath(), "eling-searchstats-data-" + tag);
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(_dataDir);

        _originalDataDir = Environment.GetEnvironmentVariable(DataDirEnv);
        Environment.SetEnvironmentVariable(DataDirEnv, _dataDir);

        File.WriteAllText(Path.Combine(_workspace, "App.cs"), "using System;\n\nclass App { public void Run() { } }\n");
        File.WriteAllText(Path.Combine(_workspace, "Helper.cs"), "class Helper { }\n");

        var index = new CodebaseIndexService(
            _workspace,
            new SqliteCodebaseIndex(ElingPaths.ResolveCodebaseDbPath(_workspace)));
        await index.IndexAsync(full: true);

        var registry = new RuntimeRegistry(
            NullLogger<RuntimeRegistry>.Instance,
            UserScope.Resolve(Path.Combine(_dataDir, "user-scope")),
            new MemoryChangeBroadcaster(),
            new JsonWorkspaceRegistry(Path.Combine(_dataDir, "agent-workspaces.json")));
        _registry = registry;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(index);
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton<Bootstrap.CodebaseWatcherService>();
        builder.Services.AddSingleton<CodebaseRebuildJobRunner>();

        var app = builder.Build();
        app.MapCodebaseRoutes();
        await app.StartAsync();
        _client = app.CreateEnvelopeClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        Environment.SetEnvironmentVariable(DataDirEnv, _originalDataDir);
        CodebaseRebuildScopeTests.TryDelete(_workspace);
        CodebaseRebuildScopeTests.TryDelete(_dataDir);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ContentSearch_IncludesStatsBlock()
    {
        var res = await _client.GetAsync($"/api/codebase/search?q=Run&project={Uri.EscapeDataString(_workspace)}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync());
        var root = doc.RootElement;
        Assert.Equal("search", root.GetProperty("mode").GetString());
        Assert.True(root.TryGetProperty("stats", out var stats));

        Assert.True(stats.GetProperty("tookMs").GetInt32() >= 0);
        Assert.True(stats.GetProperty("hits").GetInt32() >= 1);
        Assert.True(stats.GetProperty("files").GetInt32() >= 1);
        Assert.True(stats.GetProperty("chunks").GetInt32() >= 1);
        Assert.Equal(1, stats.GetProperty("workspaces").GetInt32());
        Assert.Equal(1, stats.GetProperty("workspacesWithHits").GetInt32());
    }

    [Fact]
    public async Task FederatedSearch_TotalHits_IsLimitIndependent()
    {
        // The bug: totalHits was computed from the hits that survived the
        // global Take, so roots whose rows were cut contributed nothing and
        // the reported total grew with the page size. The per-root FTS passes
        // count the full matched set, so the total must not notice the limit.
        var sibling = Path.Combine(Path.GetTempPath(), "eling-searchstats-sib-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(sibling);
        try
        {
            for (var i = 0; i < 12; i++)
                File.WriteAllText(Path.Combine(_workspace, $"Loc{i}.cs"), $"class Loc{i} {{ void Zephyr() {{ }} }}\n");
            for (var i = 0; i < 15; i++)
                File.WriteAllText(Path.Combine(sibling, $"Sib{i}.cs"), $"class Sib{i} {{ void Zephyr() {{ }} }}\n");

            var local = new CodebaseIndexService(
                _workspace,
                new SqliteCodebaseIndex(ElingPaths.ResolveCodebaseDbPath(_workspace)));
            await local.IndexAsync(full: true);
            var siblingSvc = new CodebaseIndexService(
                sibling,
                new SqliteCodebaseIndex(ElingPaths.ResolveCodebaseDbPath(sibling)));
            await siblingSvc.IndexAsync(full: true);

            var now = DateTimeOffset.UtcNow;
            _registry.RecordWorkspacesForTest([new RegisteredWorkspace(string.Empty, sibling, sibling, true, now, now)]);

            int TotalHits(JsonDocument doc) => doc.RootElement.GetProperty("stats").GetProperty("totalHits").GetInt32();

            using var small = await _client.GetAsync("/api/codebase/search?q=Zephyr&scope=all&limit=1");
            using var large = await _client.GetAsync("/api/codebase/search?q=Zephyr&scope=all&limit=100");
            Assert.Equal(HttpStatusCode.OK, small.StatusCode);
            Assert.Equal(HttpStatusCode.OK, large.StatusCode);

            using var smallDoc = await JsonDocument.ParseAsync(await small.Content.ReadAsStreamAsync());
            using var largeDoc = await JsonDocument.ParseAsync(await large.Content.ReadAsStreamAsync());
            var smallTotal = TotalHits(smallDoc);
            var largeTotal = TotalHits(largeDoc);

            Assert.True(largeTotal >= 27, $"expected at least 27 total matches, got {largeTotal}");
            Assert.Equal(largeTotal, smallTotal);
        }
        finally
        {
            CodebaseRebuildScopeTests.TryDelete(sibling);
        }
    }

    [Fact]
    public async Task FederatedPathSearch_TotalHits_IsLimitIndependent()
    {
        var sibling = Path.Combine(Path.GetTempPath(), "eling-searchstats-sibp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(sibling);
        try
        {
            for (var i = 0; i < 12; i++)
                File.WriteAllText(Path.Combine(_workspace, $"ZephyrPath{i}.cs"), $"class ZephyrPath{i} {{ }}\n");
            for (var i = 0; i < 15; i++)
                File.WriteAllText(Path.Combine(sibling, $"ZephyrPath{i}.cs"), $"class ZephyrPath{i} {{ }}\n");

            var local = new CodebaseIndexService(
                _workspace,
                new SqliteCodebaseIndex(ElingPaths.ResolveCodebaseDbPath(_workspace)));
            await local.IndexAsync(full: true);
            var siblingSvc = new CodebaseIndexService(
                sibling,
                new SqliteCodebaseIndex(ElingPaths.ResolveCodebaseDbPath(sibling)));
            await siblingSvc.IndexAsync(full: true);

            var now = DateTimeOffset.UtcNow;
            _registry.RecordWorkspacesForTest([new RegisteredWorkspace(string.Empty, sibling, sibling, true, now, now)]);

            using var small = await _client.GetAsync("/api/codebase/search?q=ZephyrPath&search=path&scope=all&limit=1");
            using var large = await _client.GetAsync("/api/codebase/search?q=ZephyrPath&search=path&scope=all&limit=100");
            Assert.Equal(HttpStatusCode.OK, small.StatusCode);
            Assert.Equal(HttpStatusCode.OK, large.StatusCode);

            using var smallDoc = await JsonDocument.ParseAsync(await small.Content.ReadAsStreamAsync());
            using var largeDoc = await JsonDocument.ParseAsync(await large.Content.ReadAsStreamAsync());

            Assert.Equal(
                largeDoc.RootElement.GetProperty("stats").GetProperty("totalHits").GetInt32(),
                smallDoc.RootElement.GetProperty("stats").GetProperty("totalHits").GetInt32());
            Assert.True(largeDoc.RootElement.GetProperty("stats").GetProperty("totalHits").GetInt32() >= 27);
        }
        finally
        {
            CodebaseRebuildScopeTests.TryDelete(sibling);
        }
    }

    [Fact]
    public async Task FileSearch_IncludesStatsBlock()
    {
        var res = await _client.GetAsync($"/api/codebase/search?q=App&search=file&project={Uri.EscapeDataString(_workspace)}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync());
        var root = doc.RootElement;
        Assert.Equal("files", root.GetProperty("mode").GetString());
        Assert.True(root.TryGetProperty("stats", out var stats));

        Assert.True(stats.GetProperty("tookMs").GetInt32() >= 0);
        Assert.Equal(1, stats.GetProperty("hits").GetInt32());
        Assert.Equal(1, stats.GetProperty("files").GetInt32());
        Assert.True(stats.GetProperty("chunks").GetInt32() >= 1);
        Assert.Equal(1, stats.GetProperty("workspaces").GetInt32());
        Assert.Equal(1, stats.GetProperty("workspacesWithHits").GetInt32());
    }

    [Fact]
    public async Task ContentSearch_WhenHitsExceedLimit_ReportsHitsAndTotalHits()
    {
        for (int i = 0; i < 25; i++)
        {
            File.WriteAllText(Path.Combine(_workspace, $"Test{i}.cs"), $"class Test{i} {{ void Run() {{ }} }}\n");
        }
        var index = new CodebaseIndexService(
            _workspace,
            new SqliteCodebaseIndex(ElingPaths.ResolveCodebaseDbPath(_workspace)));
        await index.IndexAsync(full: true);

        var res = await _client.GetAsync($"/api/codebase/search?q=Run&limit=10&project={Uri.EscapeDataString(_workspace)}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync());
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("stats", out var stats));

        Assert.Equal(10, stats.GetProperty("hits").GetInt32());
        Assert.True(stats.GetProperty("totalHits").GetInt32() >= 25);
    }
}
