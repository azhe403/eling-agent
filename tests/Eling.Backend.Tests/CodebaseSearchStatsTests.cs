using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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

[Collection(ElingDataDirCollection.Name)]
public sealed class CodebaseSearchStatsTests : IAsyncLifetime
{
    private const string DataDirEnv = "ELING_DATA_DIR";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private string _workspace = null!;
    private string _dataDir = null!;
    private string? _originalDataDir;
    private HttpClient _client = null!;

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
            UserScope.Resolve(Path.Combine(_dataDir, "user-scope")));

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
        _client = app.GetTestClient();
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
    public async Task TopMode_IncludesStatsBlock()
    {
        var res = await _client.GetAsync($"/api/codebase/search?q=&project={Uri.EscapeDataString(_workspace)}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync());
        var root = doc.RootElement;
        Assert.Equal("top", root.GetProperty("mode").GetString());
        Assert.True(root.TryGetProperty("stats", out var stats));
        Assert.True(stats.GetProperty("tookMs").GetInt32() >= 0);
        Assert.True(stats.GetProperty("files").GetInt32() >= 1);
        Assert.True(stats.GetProperty("chunks").GetInt32() >= 1);
        Assert.Equal(1, stats.GetProperty("workspaces").GetInt32());
    }
}
