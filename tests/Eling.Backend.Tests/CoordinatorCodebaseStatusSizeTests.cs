using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Eling.Backend.Controllers;
using Eling.Core.Codebase;
using Eling.Core.Projects;
using Eling.Core.Scope;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Eling.Backend.Tests;

/// <summary>
/// The workspaces page tells the user what each index costs on disk. The
/// number has to come from the file, not from the index: a database that is
/// corrupt still occupies disk, and a workspace nobody can search any more
/// still has to be worth deleting.
/// </summary>
[Collection(ElingDataDirCollection.Name)]
public sealed class CoordinatorCodebaseStatusSizeTests : IAsyncLifetime
{
    private const string DataDirEnv = "ELING_DATA_DIR";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private string _root = null!;
    private string _dataDir = null!;
    private string? _originalDataDir;
    private HttpClient _client = null!;
    private RuntimeRegistry _registry = null!;
    private JsonWorkspaceRegistry _workspaces = null!;

    public async Task InitializeAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        _root = Path.Combine(Path.GetTempPath(), "eling-cbsize-" + tag);
        _dataDir = Path.Combine(_root, "data");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_dataDir);

        // Rooted at an isolated data dir, so the index path the endpoint
        // resolves never points at the real Eling store.
        _originalDataDir = Environment.GetEnvironmentVariable(DataDirEnv);
        Environment.SetEnvironmentVariable(DataDirEnv, _dataDir);

        _workspaces = new JsonWorkspaceRegistry(Path.Combine(_dataDir, "agent-workspaces.json"));
        _registry = new RuntimeRegistry(
            NullLogger<RuntimeRegistry>.Instance,
            UserScope.Resolve(Path.Combine(_dataDir, "user-scope")),
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
        Environment.SetEnvironmentVariable(DataDirEnv, _originalDataDir);
        CodebaseRebuildScopeTests.TryDelete(_root);
        await Task.CompletedTask;
    }

    /// <summary>
    /// A workspace folder whose name is not one of the product's own transient
    /// prefixes, so it counts as a project and is listed.
    /// </summary>
    private string Workspace(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        var now = DateTimeOffset.UtcNow;
        _registry.RecordWorkspacesForTest([
            new RegisteredWorkspace(string.Empty, path, path, CodebaseEnabled: true, FirstSeenAt: now, LastSeenAt: now)
        ]);
        return path;
    }

    private async Task<JsonElement?> StatusForAsync(string workspaceRoot)
    {
        var res = await _client.GetAsync("/api/coordinator/projects/codebase-statuses");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var entries = await res.Content.ReadFromJsonAsync<List<JsonElement>>(Json) ?? [];
        return entries.FirstOrDefault(e =>
            string.Equals(e.GetProperty("workspaceRoot").GetString(), workspaceRoot, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Status_ReportsTheDatabaseFilePlusItsWal()
    {
        var workspace = Workspace("size-wal-ws");

        // Bytes, not a real index: the size is a property of the file, so the
        // number must not depend on the database being readable.
        var dbPath = ElingPaths.ResolveCodebaseDbPath(workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        File.WriteAllBytes(dbPath, new byte[4096]);
        File.WriteAllBytes(dbPath + "-wal", new byte[1500]);

        var status = await StatusForAsync(workspace);

        Assert.NotNull(status);
        Assert.Equal(4096 + 1500, status!.Value.GetProperty("dbSizeBytes").GetInt64());
    }

    [Fact]
    public async Task Status_ForARealIndex_ReportsAtLeastTheDatabaseFileSize()
    {
        var workspace = Workspace("size-real-ws");
        File.WriteAllText(Path.Combine(workspace, "a.cs"), "class A { }\n");
        File.WriteAllText(Path.Combine(workspace, "b.cs"), "class B { }\n");

        var dbPath = ElingPaths.ResolveCodebaseDbPath(workspace);
        var service = new CodebaseIndexService(
            workspace,
            new SqliteCodebaseIndex(dbPath));
        await service.IndexAsync(full: true);

        var status = await StatusForAsync(workspace);

        Assert.NotNull(status);
        Assert.True(status!.Value.GetProperty("files").GetInt32() > 0);
        var reported = status.Value.GetProperty("dbSizeBytes").GetInt64();
        Assert.True(reported > 0);
        // The WAL is counted on top of the file, so the total is never below
        // the file's own length.
        Assert.True(reported >= new FileInfo(dbPath).Length);
    }

    [Fact]
    public async Task Status_ForAWorkspaceWithoutAnIndex_ReportsNoSize()
    {
        var workspace = Workspace("size-none-ws");

        var status = await StatusForAsync(workspace);

        Assert.NotNull(status);
        Assert.Equal(0, status!.Value.GetProperty("dbSizeBytes").GetInt64());
    }
}
