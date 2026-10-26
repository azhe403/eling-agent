using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Eling.Backend.Controllers;
using Eling.Backend.Dtos;
using Eling.Core.Runtime;
using Eling.Core.Scope;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Eling.Backend.Tests;

[Collection(ElingDataDirCollection.Name)]
public sealed class WorkspaceTreeControllerTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private string _root = null!;
    private HttpClient _client = null!;
    private RuntimeRegistry _registry = null!;
    private Eling.Core.Projects.JsonWorkspaceRegistry _workspaces = null!;

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "eling-wscoord-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);

        var dataDir = Path.Combine(_root, "data");
        var userScope = UserScope.Resolve(Path.Combine(dataDir, "user-scope"));
        _workspaces = new Eling.Core.Projects.JsonWorkspaceRegistry(
            Path.Combine(dataDir, "agent-workspaces.json"));
        _registry = new RuntimeRegistry(
            NullLogger<RuntimeRegistry>.Instance,
            userScope,
            workspaces: _workspaces);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_registry);
        builder.Services.AddSingleton(new MemoryChangeBroadcaster());
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
    };

    private async Task<JsonElement[]> GetWorkspaces(string query = "")
    {
        var path = $"/api/coordinator/workspaces{query}";
        using var response = await _client.GetAsync(path, HttpCompletionOption.ResponseContentRead);
        var body = await response.Content.ReadAsStringAsync();
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException($"{path} → {(int)response.StatusCode}: {body}");
        }
        using var doc = JsonDocument.Parse(body);
        var roots = new List<JsonElement>();
        foreach (var item in doc.RootElement.GetProperty("rows").EnumerateArray())
        {
            roots.Add(item.Clone());
        }
        return [.. roots];
    }

    [Fact]
    public async Task Workspaces_ReturnsAllThreeTreesAndRows()
    {
        var live = Workspace("alpha");
        var closed = Workspace("beta");
        _registry.Register(Registration(live, 801));
        _registry.Register(Registration(closed, 802));
        _registry.Unregister(802);

        using var response = await _client.GetAsync("/api/coordinator/workspaces");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        var rows = doc.RootElement.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);

        var flat = doc.RootElement.GetProperty("flat").EnumerateArray().ToList();
        Assert.Equal(2, flat.Count);

        var repo = doc.RootElement.GetProperty("repo").EnumerateArray().ToList();
        Assert.Equal(2, repo.Count);

        var folder = doc.RootElement.GetProperty("folder").EnumerateArray().ToList();
        Assert.Equal(2, folder.Count);
    }

    [Fact]
    public async Task Workspaces_ExcludesUserScopeSentinel()
    {
        _registry.Register(new RuntimeRegistration
        {
            ProcessId = 810,
            HeadScopeRoot = "UserScope",
            WorkspaceRoot = "UserScope",
            CodebaseEnabled = true,
            DataDirectory = Path.Combine(_root, "user-scope"),
            StartTime = DateTimeOffset.UtcNow,
        });

        using var response = await _client.GetAsync("/api/coordinator/workspaces");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        var rows = doc.RootElement.GetProperty("rows");
        Assert.Equal(0, rows.GetArrayLength());
    }

    [Fact]
    public async Task Workspaces_ExcludesDisabledCodebase()
    {
        var workspace = Workspace("no-codebase");
        _registry.Register(new RuntimeRegistration
        {
            ProcessId = 820,
            HeadScopeRoot = workspace,
            WorkspaceRoot = workspace,
            CodebaseEnabled = false,
            DataDirectory = Path.Combine(workspace, ".eling"),
            StartTime = DateTimeOffset.UtcNow,
        });

        using var response = await _client.GetAsync("/api/coordinator/workspaces");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        var rows = doc.RootElement.GetProperty("rows");
        Assert.Equal(0, rows.GetArrayLength());
    }

    [Fact]
    public async Task Workspaces_StatusActive_FiltersToLiveOnly()
    {
        var live = Workspace("live-ws");
        var closed = Workspace("closed-ws");
        _registry.Register(Registration(live, 831));
        _registry.Register(Registration(closed, 832));
        _registry.Unregister(832);

        using var response = await _client.GetAsync("/api/coordinator/workspaces?status=active");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        var rows = doc.RootElement.GetProperty("rows").EnumerateArray().ToList();
        Assert.Single(rows);
        Assert.Equal(live, rows[0].GetProperty("root").GetString());
    }

    [Fact]
    public async Task Workspaces_StatusOffline_FiltersToClosedOnly()
    {
        var live = Workspace("live-ws2");
        var closed = Workspace("closed-ws2");
        _registry.Register(Registration(live, 841));
        _registry.Register(Registration(closed, 842));
        _registry.Unregister(842);

        using var response = await _client.GetAsync("/api/coordinator/workspaces?status=offline");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        var rows = doc.RootElement.GetProperty("rows").EnumerateArray().ToList();
        Assert.Single(rows);
        Assert.Equal(closed, rows[0].GetProperty("root").GetString());
    }

    [Fact]
    public async Task Workspaces_InvalidStatus_Returns400()
    {
        using var response = await _client.GetAsync("/api/coordinator/workspaces?status=bogus");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Workspaces_QueryMatchesNameRootOrPid()
    {
        var ws1 = Workspace("alpha");
        var ws2 = Workspace("beta");
        _registry.Register(Registration(ws1, 901));
        _registry.Register(Registration(ws2, 902));

        // Match by name fragment.
        using (var response = await _client.GetAsync("/api/coordinator/workspaces?q=alph"))
        {
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var rows = doc.RootElement.GetProperty("rows").EnumerateArray().ToList();
            Assert.Single(rows);
            Assert.Equal(ws1, rows[0].GetProperty("root").GetString());
        }

        // Match by PID.
        using (var response = await _client.GetAsync("/api/coordinator/workspaces?q=902"))
        {
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var rows = doc.RootElement.GetProperty("rows").EnumerateArray().ToList();
            Assert.Single(rows);
            Assert.Equal(ws2, rows[0].GetProperty("root").GetString());
        }
    }

    [Fact]
    public async Task Workspaces_DedupesMultipleProcessesInSameWorkspace()
    {
        var ws = Workspace("dedup");
        _registry.Register(Registration(ws, 951));
        _registry.Register(Registration(ws, 952));

        using var response = await _client.GetAsync("/api/coordinator/workspaces");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        var rows = doc.RootElement.GetProperty("rows").EnumerateArray().ToList();
        var row = Assert.Single(rows);
        Assert.Equal(ws, row.GetProperty("root").GetString());
        Assert.Equal(2, row.GetProperty("processCount").GetInt32());
    }

    [Fact]
    public async Task Workspaces_FlatTree_SortsByLabel()
    {
        var alpha = Workspace("alpha");
        var zeta = Workspace("zeta");
        _registry.Register(Registration(zeta, 961));
        _registry.Register(Registration(alpha, 962));

        using var response = await _client.GetAsync("/api/coordinator/workspaces");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        var flat = doc.RootElement.GetProperty("flat").EnumerateArray().ToList();
        Assert.Equal(["alpha", "zeta"], flat.Select(n => n.GetProperty("label").GetString()));
    }

    [Fact]
    public async Task Workspaces_RepoTree_GroupsWorktreesUnderMainCheckout()
    {
        // Set up a git repo so ResolveRepositoryRoot can pick it up.
        var mainWs = Workspace("repo");
        var wtWs = Workspace("feature");
        _registry.Register(Registration(mainWs, 971));
        _registry.Register(Registration(wtWs, 972));

        using var response = await _client.GetAsync("/api/coordinator/workspaces");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        var repo = doc.RootElement.GetProperty("repo").EnumerateArray().ToList();
        // Without a git repo, both are their own group; both are top-level.
        Assert.Equal(2, repo.Count);
    }
}
