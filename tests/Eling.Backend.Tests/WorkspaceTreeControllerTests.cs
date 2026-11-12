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
        builder.Services.AddSingleton(new Eling.Core.Workspaces.WorkspaceExpansionStore(
            Path.Combine(_root, "workspace-expansion.json")));
        builder.Services
            .AddControllers()
            .AddApplicationPart(typeof(CoordinatorController).Assembly)
            .AddJsonOptions(o => o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

        var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        _client = app.CreateEnvelopeClient();
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

    private async Task<JsonElement[]> GetRows(string query = "")
    {
        var path = $"/api/coordinator/workspaces?{query}";
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

    private async Task<JsonElement> GetNodes(string group, string extra = "")
    {
        var path = $"/api/coordinator/workspaces?group={group}{extra}";
        using var response = await _client.GetAsync(path, HttpCompletionOption.ResponseContentRead);
        var body = await response.Content.ReadAsStringAsync();
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException($"{path} → {(int)response.StatusCode}: {body}");
        }
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("nodes").Clone();
    }

    [Fact]
    public async Task Workspaces_ReturnsRowsAndSingleNodeArrangement()
    {
        var live = Workspace("alpha");
        var closed = Workspace("beta");
        _registry.Register(Registration(live, 801));
        _registry.Register(Registration(closed, 802));
        _registry.Unregister(802);

        var flat = await GetNodes("flat");
        Assert.Equal(2, flat.EnumerateArray().Count());

        var repo = await GetNodes("repo");
        Assert.Equal(2, repo.EnumerateArray().Count());

        var folder = await GetNodes("folder");
        Assert.Equal(2, folder.EnumerateArray().Count());
    }

    [Fact]
    public async Task Workspaces_MissingGroup_Returns400()
    {
        using var response = await _client.GetAsync("/api/coordinator/workspaces");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Workspaces_InvalidGroup_Returns400()
    {
        using var response = await _client.GetAsync("/api/coordinator/workspaces?group=bogus");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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

        var rows = await GetRows("group=flat");
        Assert.Empty(rows);
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

        var rows = await GetRows("group=flat");
        Assert.Empty(rows);
    }

    [Fact]
    public async Task Workspaces_StatusActive_FiltersToLiveOnly()
    {
        var live = Workspace("live-ws");
        var closed = Workspace("closed-ws");
        _registry.Register(Registration(live, 831));
        _registry.Register(Registration(closed, 832));
        _registry.Unregister(832);

        var rows = await GetRows("group=flat&status=active");
        var single = Assert.Single(rows);
        Assert.Equal(live, single.GetProperty("root").GetString());
    }

    [Fact]
    public async Task Workspaces_StatusOffline_FiltersToClosedOnly()
    {
        var live = Workspace("live-ws2");
        var closed = Workspace("closed-ws2");
        _registry.Register(Registration(live, 841));
        _registry.Register(Registration(closed, 842));
        _registry.Unregister(842);

        var rows = await GetRows("group=flat&status=offline");
        var single = Assert.Single(rows);
        Assert.Equal(closed, single.GetProperty("root").GetString());
    }

    [Fact]
    public async Task Workspaces_InvalidStatus_Returns400()
    {
        using var response = await _client.GetAsync("/api/coordinator/workspaces?group=flat&status=bogus");
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
        var byName = await GetRows("group=flat&q=alph");
        var nameRow = Assert.Single(byName);
        Assert.Equal(ws1, nameRow.GetProperty("root").GetString());

        // Match by PID.
        var byPid = await GetRows("group=flat&q=902");
        var pidRow = Assert.Single(byPid);
        Assert.Equal(ws2, pidRow.GetProperty("root").GetString());
    }

    [Fact]
    public async Task Workspaces_DedupesMultipleProcessesInSameWorkspace()
    {
        var ws = Workspace("dedup");
        _registry.Register(Registration(ws, 951));
        _registry.Register(Registration(ws, 952));

        var rows = await GetRows("group=flat");
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

        var flat = await GetNodes("flat");
        Assert.Equal(["alpha", "zeta"], flat.EnumerateArray().Select(n => n.GetProperty("label").GetString()));
    }

    [Fact]
    public async Task Workspaces_RepoTree_GroupsWorktreesUnderMainCheckout()
    {
        // Set up a git repo so ResolveRepositoryRoot can pick it up.
        var mainWs = Workspace("repo");
        var wtWs = Workspace("feature");
        _registry.Register(Registration(mainWs, 971));
        _registry.Register(Registration(wtWs, 972));

        var repo = await GetNodes("repo");
        // Without a git repo, both are their own group; both are top-level.
        Assert.Equal(2, repo.EnumerateArray().Count());
    }

    [Fact]
    public async Task Workspaces_RepoTree_RepositoryWithoutMainCheckoutHasNullRoot()
    {
        // A linked worktree whose main checkout is not registered: the resolver
        // still finds the repository, but there is no row to stand for it, so the
        // repository node has no root to select and clients join on its roots.
        var main = Workspace("main");
        var mainGit = Path.Combine(main, ".git");
        var worktreeGit = Path.Combine(mainGit, "worktrees", "linked");
        Directory.CreateDirectory(worktreeGit);
        File.WriteAllText(Path.Combine(worktreeGit, "commondir"), "../..");

        var linked = Workspace("linked");
        File.WriteAllText(Path.Combine(linked, ".git"), $"gitdir: {worktreeGit}");

        _registry.Register(Registration(linked, 981));

        var nodes = await GetNodes("repo");
        var repoNode = Assert.Single(nodes.EnumerateArray());
        Assert.True(repoNode.GetProperty("isRepository").GetBoolean());
        Assert.Equal(JsonValueKind.Null, repoNode.GetProperty("root").ValueKind);
        Assert.Contains(linked, repoNode.GetProperty("roots").EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    public async Task WorkspaceExpansion_EmptyStore_ReturnsEmptyLists()
    {
        using var response = await _client.GetAsync("/api/coordinator/workspaces/expanded");
        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<WorkspaceExpansionDto>(Json);

        Assert.NotNull(dto);
        Assert.Empty(dto!.Expanded);
        Assert.Empty(dto.Collapsed);
    }

    [Fact]
    public async Task WorkspaceExpansion_PutThenGet_RoundTripsExpandedAndCollapsed()
    {
        var payload = new WorkspaceExpansionDto(
            ["repo:C:/work/main", "ws:C:/work/main"],
            ["ws:C:/work/other"]);
        using var put = await _client.PutAsJsonAsync("/api/coordinator/workspaces/expanded", payload, Json);
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var dto = await _client.GetFromJsonAsync<WorkspaceExpansionDto>(
            "/api/coordinator/workspaces/expanded", Json);

        Assert.NotNull(dto);
        Assert.Equal(payload.Expanded, dto!.Expanded);
        Assert.Equal(payload.Collapsed, dto.Collapsed);
    }

    [Fact]
    public async Task WorkspaceExpansion_Put_DropsDuplicateAndBlankIds()
    {
        var payload = new WorkspaceExpansionDto(
            ["ws:a", "ws:a", "", "  ", "ws:b"],
            []);
        using var put = await _client.PutAsJsonAsync("/api/coordinator/workspaces/expanded", payload, Json);
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var dto = await _client.GetFromJsonAsync<WorkspaceExpansionDto>(
            "/api/coordinator/workspaces/expanded", Json);

        Assert.Equal(["ws:a", "ws:b"], dto!.Expanded);
    }
}
