using Eling.Core.Memory;
using Eling.Core.Projects;
using Eling.Core.Scope;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eling.Backend.Tests;

/// <summary>
/// Locks the sweeper's catalogue hygiene rule: a workspace whose process died
/// keeps its row — a codebase index stays readable after its owner exits —
/// but a workspace whose folder is gone has nothing left to remember, and its
/// row is dropped on the next prune pass.
/// </summary>
public sealed class RuntimeRegistryWorkspacePruneTests : IDisposable
{
    private readonly string _dir;

    public RuntimeRegistryWorkspacePruneTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "eling-ws-prune-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public async Task PruneMissingWorkspaces_RemovesRowForGoneFolder_KeepsRowForLiveFolder()
    {
        // Arrange — alive is a real folder; gone was never created, which is
        // the shape a lifecycle-test temp dir leaves behind after cleanup.
        var alive = Path.Combine(_dir, "alive");
        var gone = Path.Combine(_dir, "gone");
        Directory.CreateDirectory(alive);

        using var workspaces = new JsonWorkspaceRegistry(Path.Combine(_dir, "agent-workspaces.json"));
        var now = DateTimeOffset.UtcNow;
        workspaces.Record(new RegisteredWorkspace(string.Empty, alive, alive, CodebaseEnabled: true, FirstSeenAt: now, LastSeenAt: now));
        workspaces.Record(new RegisteredWorkspace(string.Empty, gone, gone, CodebaseEnabled: true, FirstSeenAt: now, LastSeenAt: now));

        using var registry = new RuntimeRegistry(
            NullLogger<RuntimeRegistry>.Instance,
            UserScope.Resolve(Path.Combine(_dir, "user-scope")),
            new MemoryChangeBroadcaster(),
            workspaces);

        // Act
        await registry.PruneMissingWorkspacesAsync();

        // Assert
        var rows = await workspaces.ListAsync();
        Assert.Single(rows);
        Assert.Equal(alive, rows[0].WorkspaceRoot);
    }

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
