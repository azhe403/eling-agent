using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Eling.Backend.Controllers;
using Eling.Core.Projects;
using Eling.Core.Runtime;
using Eling.Core.Scope;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Eling.Backend.Tests;

/// <summary>
/// The runtime list serves two modes on one route. The default keeps memory
/// working exactly as before; <c>includeRegistered</c> is what lets the codebase
/// page list a project that is closed but still indexed.
/// </summary>
[Collection(ElingDataDirCollection.Name)]
public sealed class CoordinatorControllerTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private string _root = null!;
    private HttpClient _client = null!;
    private RuntimeRegistry _registry = null!;
    private SqliteWorkspacesRegistry _workspaces = null!;

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "eling-coord-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);

        var dataDir = Path.Combine(_root, "data");
        var userScope = UserScope.Resolve(Path.Combine(dataDir, "user-scope"));
        _workspaces = new SqliteWorkspacesRegistry(
            ElingPaths.ResolveProjectsDbPath(dataDir, elingDataDir: dataDir));
        _registry = new RuntimeRegistry(
            NullLogger<RuntimeRegistry>.Instance,
            userScope,
            workspaces: _workspaces);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_registry);
        // Controllers are discovered from the entry assembly, which under a test
        // host is the test runner; the backend assembly must be added or the
        // route 404s.
        builder.Services
            .AddControllers()
            .AddApplicationPart(typeof(CoordinatorController).Assembly)
            .AddJsonOptions(o => o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

        var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        _client = app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        _registry.Dispose();
        _workspaces.Dispose();
        CodebaseRebuildScopeTests.TryDelete(_root);
        await Task.CompletedTask;
    }

    private string Workspace(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static RuntimeRegistration Registration(string workspace, int processId) => new()
    {
        ProcessId = processId,
        HeadScopeRoot = workspace,
        WorkspaceRoot = workspace,
        CodebaseEnabled = true,
        DataDirectory = Path.Combine(workspace, ".eling"),
        StartTime = DateTimeOffset.UtcNow,
        McpEnabled = true,
        McpTransport = "stdio",
    };

    private async Task<List<JsonElement>> GetAsync(string url)
    {
        var res = await _client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<List<JsonElement>>(Json) ?? new List<JsonElement>();
    }

    private static string RootOf(JsonElement entry) => entry.GetProperty("workspaceRoot").GetString()!;

    /// <summary>
    /// The default must be unchanged: memory resolves a live service, so a
    /// project it cannot reach must not appear here.
    /// </summary>
    [Fact]
    public async Task DefaultMode_ListsOnlyLiveRuntimes()
    {
        var workspace = Workspace("live-only");
        _registry.Register(Registration(workspace, 501));

        var entries = await GetAsync("/api/coordinator/runtimes");

        Assert.Contains(entries, e => RootOf(e) == workspace);
        Assert.All(entries, e => Assert.True(e.GetProperty("isAlive").GetBoolean()));
    }

    /// <summary>
    /// The whole point: a workspace whose process has exited is still listed
    /// when asked for, because its codebase index is readable. A remembered
    /// workspace has no process, so <c>processId</c> is 0 — the marker the
    /// dashboard uses to tell "remembered" from "running".
    /// </summary>
    [Fact]
    public async Task IncludeRegistered_ListsAWorkspaceWhoseProcessIsGone()
    {
        var workspace = Workspace("closed-but-indexed");
        _registry.Register(Registration(workspace, 502));
        _registry.Unregister(502);

        Assert.Empty(await GetAsync("/api/coordinator/runtimes"));

        var entries = await GetAsync("/api/coordinator/runtimes?includeRegistered=true");

        var entry = Assert.Single(entries, e => RootOf(e) == workspace);
        Assert.False(entry.GetProperty("isAlive").GetBoolean());
        Assert.Equal(0, entry.GetProperty("processId").GetInt32());
        Assert.True(entry.GetProperty("codebaseEnabled").GetBoolean());
    }

    /// <summary>
    /// A live process wins the dedupe: its row must keep the real pid and
    /// heartbeat rather than being replaced by the remembered version.
    /// </summary>
    [Fact]
    public async Task IncludeRegistered_LiveRowWinsOverTheRememberedOne()
    {
        var workspace = Workspace("both-paths");
        _registry.Register(Registration(workspace, 503));

        var entries = await GetAsync("/api/coordinator/runtimes?includeRegistered=true");

        var entry = Assert.Single(entries, e => RootOf(e) == workspace);
        Assert.Equal(503, entry.GetProperty("processId").GetInt32());
        Assert.True(entry.GetProperty("isAlive").GetBoolean());
    }

    /// <summary>
    /// An excluded workspace was excluded when it registered, and that decision
    /// is persisted rather than recomputed — otherwise changing the exclusion
    /// list later would retroactively re-admit it.
    /// </summary>
    [Fact]
    public async Task IncludeRegistered_KeepsAnExcludedProjectExcluded()
    {
        var workspace = Workspace("opted-out");
        var registration = Registration(workspace, 504);
        registration.CodebaseEnabled = false;
        _registry.Register(registration);
        _registry.Unregister(504);

        var entries = await GetAsync("/api/coordinator/runtimes?includeRegistered=true");

        Assert.False(Assert.Single(entries).GetProperty("codebaseEnabled").GetBoolean());
    }

    /// <summary>
    /// A row whose folder is gone stays listed. Deciding what a missing folder
    /// means belongs to the consumer, and pruning here would make the tiles
    /// shrink with no explanation.
    /// </summary>
    [Fact]
    public async Task IncludeRegistered_StillListsAProjectWhoseFolderIsGone()
    {
        var workspace = Workspace("deleted-folder");
        _registry.Register(Registration(workspace, 505));
        _registry.Unregister(505);
        Directory.Delete(workspace);

        var entries = await GetAsync("/api/coordinator/runtimes?includeRegistered=true");

        Assert.Single(entries, e => RootOf(e) == workspace);
    }

    /// <summary>
    /// The UserScope sentinel and the global data root are excluded from project
    /// lists in Alive(); a remembered row must be held to the same rule.
    /// </summary>
    [Fact]
    public async Task IncludeRegistered_ExcludesTheUserScopeSentinel()
    {
        var registration = new RuntimeRegistration
        {
            ProcessId = 506,
            HeadScopeRoot = "UserScope",
            WorkspaceRoot = "UserScope",
            CodebaseEnabled = true,
            DataDirectory = Path.Combine(_root, "global", ".eling"),
            StartTime = DateTimeOffset.UtcNow,
            McpEnabled = true,
            McpTransport = "stdio",
        };
        _registry.Register(registration);
        _registry.Unregister(506);

        var entries = await GetAsync("/api/coordinator/runtimes?includeRegistered=true");

        Assert.DoesNotContain(entries, e => RootOf(e) == "UserScope");
    }

    [Fact]
    public async Task IncludeRegistered_ExcludesOpenCodeConfigDirectories()
    {
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var opencodeDir = Path.Combine(userHome, ".config", "opencode");
        var registration = Registration(opencodeDir, 507);
        _registry.Register(registration);

        var entries = await GetAsync("/api/coordinator/runtimes?includeRegistered=true");

        Assert.DoesNotContain(entries, e => RootOf(e) == opencodeDir);
    }

    /// <summary>No param, no registry configured: the endpoint still answers.</summary>
    [Fact]
    public async Task DefaultMode_WorksWithoutARegistry()
    {
        var res = await _client.GetAsync("/api/coordinator/runtimes");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }
}
