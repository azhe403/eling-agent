using Eling.Core.Memory;
using Eling.Core.Projects;
using Eling.Core.Runtime;
using Eling.Core.Scope;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eling.Backend.Tests;

/// <summary>
/// Locks the workspace-catalogue recording rule: a runtime registration adds
/// exactly one row — the folder the runtime actually runs in. Ancestor levels
/// of the <c>.eling</c> scope chain are memory scopes, not workspaces, and must
/// never enter the catalogue just because a child workspace registered. An
/// ancestor stays reachable as a memory scope through the chain walk and MCP
/// recall fan-out; neither reads this catalogue.
/// </summary>
public sealed class RuntimeRegistryWorkspaceRecordingTests : IDisposable
{
    private readonly string _dir;

    public RuntimeRegistryWorkspaceRecordingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "eling-ws-record-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    /// <summary>
    /// The AZ shape: the workspace has no own <c>.eling</c> and borrows the
    /// ancestor's. The ancestor is recorded as the row's head scope — a field,
    /// not a row of its own.
    /// </summary>
    [Fact]
    public async Task Register_WorkspaceWithoutOwnScope_RecordsOneRow_NotTheAncestor()
    {
        var ancestor = Path.Combine(_dir, "ancestor");
        var workspace = Path.Combine(ancestor, "service");
        Directory.CreateDirectory(Path.Combine(ancestor, ".eling"));
        Directory.CreateDirectory(workspace);

        using var workspaces = new JsonWorkspaceRegistry(Path.Combine(_dir, "agent-workspaces.json"));
        using var registry = NewRegistry(workspaces);

        registry.Register(Registration(headScopeRoot: ancestor, workspaceRoot: workspace, dataDirectory: Path.Combine(ancestor, ".eling")));

        var rows = await workspaces.ListAsync();
        var row = Assert.Single(rows);
        Assert.Equal(workspace, row.WorkspaceRoot);
        Assert.Equal(ancestor, row.HeadScopeRoot);
    }

    /// <summary>
    /// The own-scope shape: the chain is [workspace, ancestor], and only the
    /// workspace is a catalogue row.
    /// </summary>
    [Fact]
    public async Task Register_WorkspaceWithOwnScope_RecordsOneRow_NotTheChain()
    {
        var ancestor = Path.Combine(_dir, "ancestor");
        var workspace = Path.Combine(ancestor, "service");
        Directory.CreateDirectory(Path.Combine(ancestor, ".eling"));
        Directory.CreateDirectory(Path.Combine(workspace, ".eling"));

        using var workspaces = new JsonWorkspaceRegistry(Path.Combine(_dir, "agent-workspaces.json"));
        using var registry = NewRegistry(workspaces);

        registry.Register(Registration(headScopeRoot: workspace, workspaceRoot: workspace, dataDirectory: Path.Combine(workspace, ".eling")));

        var rows = await workspaces.ListAsync();
        var row = Assert.Single(rows);
        Assert.Equal(workspace, row.WorkspaceRoot);
        Assert.Equal(workspace, row.HeadScopeRoot);
    }

    private RuntimeRegistry NewRegistry(JsonWorkspaceRegistry workspaces) => new(
        NullLogger<RuntimeRegistry>.Instance,
        UserScope.Resolve(Path.Combine(_dir, "user-scope")),
        new MemoryChangeBroadcaster(),
        workspaces);

    private static RuntimeRegistration Registration(string headScopeRoot, string workspaceRoot, string dataDirectory) => new()
    {
        ProcessId = Environment.ProcessId,
        HeadScopeRoot = headScopeRoot,
        WorkspaceRoot = workspaceRoot,
        CodebaseEnabled = true,
        DataDirectory = dataDirectory,
        StartTime = DateTimeOffset.UtcNow,
        McpEnabled = true,
        McpTransport = "stdio",
    };

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup; the OS reclaims the temp root eventually.
        }
    }
}
