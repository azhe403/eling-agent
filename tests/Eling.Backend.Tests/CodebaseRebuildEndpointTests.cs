using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Eling.Backend.Bootstrap;
using Eling.Backend.Codebase;
using Eling.Backend.Dtos;
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
/// Exercises the rebuild endpoint over real HTTP through an in-memory test
/// server: route matching, query-string binding, the 202 body shape, and the
/// JSON frames the rebuild SSE channel emits. The index seam is stubbed, so no
/// project files are read and no DB is written.
/// </summary>
[Collection(ElingDataDirCollection.Name)]
public sealed class CodebaseRebuildEndpointTests : IAsyncLifetime
{
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(), "eling-rebuild-endpoint-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _dataDir = Path.Combine(
        Path.GetTempPath(), "eling-rebuild-endpoint-data-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string? _originalDataDir =
        Environment.GetEnvironmentVariable("ELING_DATA_DIR");

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private CodebaseRebuildBroadcaster _broadcaster = null!;
    // Disposed before the scratch dirs are deleted: its 15s liveness sweeper
    // keeps writing heartbeat files into the user-scope runtime directory.
    private RuntimeRegistry _registry = null!;

    // Mirrors the stream's compact camelCase options; a frame that is not
    // single-line JSON fails to deserialize, which is the point of the test.
    private static readonly JsonSerializerOptions SseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(_dataDir);
        Environment.SetEnvironmentVariable("ELING_DATA_DIR", _dataDir);

        var broadcaster = new CodebaseRebuildBroadcaster();
        _broadcaster = broadcaster;
        var local = new CodebaseIndexService(
            _workspace,
            new SqliteCodebaseIndex(ElingPaths.ResolveCodebaseDbPath(_workspace)));
        var registry = new RuntimeRegistry(
            NullLogger<RuntimeRegistry>.Instance,
            UserScope.Resolve(Path.Combine(_dataDir, "user-scope")));
        _registry = registry;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(local);
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton(broadcaster);
        builder.Services.AddSingleton(new MemoryChangeBroadcaster());
        builder.Services.AddSingleton<IMemoryChangeNotifier>(NullMemoryChangeNotifier.Instance);
        builder.Services.AddSingleton<CodebaseWatcherService>();
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
        // Only the codebase routes plus the SSE stream. The full dashboard map
        // pulls in unrelated endpoint groups whose handler parameters are
        // resolved through the full DI graph; this test registers a deliberately
        // small one, so those groups would mis-infer their service parameters.
        _app.MapCodebaseRoutes();
        _app.MapSseEvents();
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        _registry.Dispose();
        if (_originalDataDir is null) Environment.SetEnvironmentVariable("ELING_DATA_DIR", null);
        else Environment.SetEnvironmentVariable("ELING_DATA_DIR", _originalDataDir);
        CodebaseRebuildScopeTests.TryDelete(_workspace);
        CodebaseRebuildScopeTests.TryDelete(_dataDir);
    }

    [Fact]
    public async Task PostWithoutParameters_AcceptsAndTargetsOwnWorkspace()
    {
        var response = await _client.PostAsync("/api/codebase/rebuild-index", content: null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CodebaseRebuildProgress>();
        Assert.NotNull(body);
        Assert.Equal("project", body!.Scope);
        Assert.Equal(1, body.Total);
        Assert.False(body.Full);
        Assert.NotEmpty(body.JobId);
    }

    [Fact]
    public async Task PostWithFullFlag_BindsToTheRequest()
    {
        var response = await _client.PostAsync("/api/codebase/rebuild-index?full=true", content: null);

        var body = await response.Content.ReadFromJsonAsync<CodebaseRebuildProgress>();
        Assert.True(body!.Full);
    }

    [Fact]
    public async Task PostWithNamedProject_AcceptsThatProjectAsTheOnlyTarget()
    {
        var other = Path.Combine(_dataDir, "other-project");
        Directory.CreateDirectory(other);

        var response = await _client.PostAsync(
            $"/api/codebase/rebuild-index?project={Uri.EscapeDataString(other)}", content: null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CodebaseRebuildProgress>();
        Assert.Equal("projects", body!.Scope);
        Assert.Equal(1, body.Total);
    }

    [Fact]
    public async Task GetJobStatus_AfterAccepting_ReturnsTheSameJob()
    {
        var accepted = await _client.PostAsync("/api/codebase/rebuild-index", content: null);
        var started = await accepted.Content.ReadFromJsonAsync<CodebaseRebuildProgress>();

        var fetched = await _client.GetAsync($"/api/codebase/rebuild-index/{started!.JobId}");

        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        var body = await fetched.Content.ReadFromJsonAsync<CodebaseRebuildProgress>();
        Assert.Equal(started.JobId, body!.JobId);
    }

    [Fact]
    public async Task GetUnknownJob_ReturnsNotFound()
    {
        var response = await _client.GetAsync("/api/codebase/rebuild-index/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RebuildEventStream_EmitsSingleLineJsonFrames()
    {
        using var stream = await _client.GetAsync(
            "/api/events/codebase-rebuild",
            HttpCompletionOption.ResponseHeadersRead);
        stream.EnsureSuccessStatusCode();

        using var reader = new StreamReader(await stream.Content.ReadAsStreamAsync());
        Assert.Equal("data: connected", await reader.ReadLineAsync());

        var published = new CodebaseRebuildProgress(
            "job-under-test",
            "projects",
            Full: true,
            IsRunning: false,
            Done: 1,
            Total: 1,
            SkippedRoots: [],
            CurrentProject: null,
            Results: [new CodebaseRebuildProjectResult(@"C:\repos\alpha", true, 3, 7, 2, 1, null)],
            Error: null);

        // The SSE writer registers its subscription on a background task *after*
        // the "connected" frame, so a publish issued immediately after reading
        // that frame can legitimately land before registration and be dropped.
        // Settle first (the same guard the existing memory SSE test uses), and
        // keep republishing with a bounded read so a dropped publish costs one
        // iteration instead of hanging.
        await Task.Delay(200);
        CodebaseRebuildProgress? seen = null;
        for (var i = 0; i < 5 && seen is null; i++)
        {
            _broadcaster.Publish(published);
            using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            string? line;
            try
            {
                line = await reader.ReadLineAsync(readTimeout.Token);
            }
            catch (OperationCanceledException)
            {
                continue;
            }
            if (line is null) break;
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            var json = line["data: ".Length..];
            if (json == "connected") continue;
            seen = JsonSerializer.Deserialize<CodebaseRebuildProgress>(
                json, SseJsonOptions);
        }

        Assert.NotNull(seen);
        Assert.Equal("job-under-test", seen!.JobId);
        Assert.Equal("projects", seen.Scope);
        Assert.True(seen.Full);
        Assert.False(seen.IsRunning);
        Assert.Equal(1, seen.Done);
        Assert.Equal(1, seen.Total);
        var result = Assert.Single(seen.Results);
        Assert.True(result.Ok);
        Assert.Equal(3, result.Files);
    }
}
