using Eling.Core.Memory;
using Eling.Core.Projects;
using Eling.Core.Runtime;
using Eling.Core.Scope;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eling.Backend.Tests;

/// <summary>
/// Locks the sweeper's catalogue hygiene rule: a workspace whose process died
/// keeps its row — a codebase index stays readable after its owner exits —
/// but a workspace whose folder is gone has nothing left to remember, and its
/// row is dropped on the next prune pass. A scratch workspace is the one
/// exception: once its process is gone its row goes too, even when its folder
/// is still on disk, because it has no index and no memories to keep, and it
/// is what makes the dashboard's workspace list grow with folders nobody can
/// open.
/// </summary>
public sealed class RuntimeRegistryWorkspacePruneTests : IDisposable
{
    private readonly string _dir;

    public RuntimeRegistryWorkspacePruneTests()
    {
        // Deliberately NOT under the OS temp directory: a folder there is a
        // scratch workspace, and this test needs a stand-in for a real
        // project. The scratch case has its own test below.
        _dir = Path.Combine(AppContext.BaseDirectory, "ws-prune-" + Guid.NewGuid().ToString("N")[..8]);
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

    /// <summary>
    /// The rule that keeps the dashboard's workspace list honest. A folder
    /// under the OS temp directory is scratch space — a validation run, a
    /// smoke test, a test fixture — and the folder outlives the process that
    /// made it, so the catalogue's "folder is GONE" rule would never catch
    /// it. The row has to go for a different reason, or every scratch
    /// workspace that ever ran stays listed forever.
    /// </summary>
    [Fact]
    public async Task PruneMissingWorkspaces_RemovesScratchRow_KeepsTheProjectRow()
    {
        // Arrange — the scratch folder is real and still on disk, which is
        // exactly why the "folder is GONE" rule does not catch it.
        var scratch = Path.Combine(Path.GetTempPath(), "eling-prune-" + Guid.NewGuid().ToString("N")[..8]);
        var project = Path.Combine(_dir, "project");
        Directory.CreateDirectory(scratch);
        Directory.CreateDirectory(project);

        try
        {
            using var workspaces = new JsonWorkspaceRegistry(Path.Combine(_dir, "agent-workspaces.json"));
            var now = DateTimeOffset.UtcNow;
            workspaces.Record(new RegisteredWorkspace(string.Empty, scratch, scratch, CodebaseEnabled: true, FirstSeenAt: now, LastSeenAt: now));
            workspaces.Record(new RegisteredWorkspace(string.Empty, project, project, CodebaseEnabled: true, FirstSeenAt: now, LastSeenAt: now));

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
            Assert.Equal(project, rows[0].WorkspaceRoot);
        }
        finally
        {
            // In a finally, not after the asserts: a failing assert would
            // otherwise leave the scratch folder behind, which is the very
            // litter this rule exists to remove.
            TryDelete(scratch);
        }
    }

    /// <summary>
    /// The other half of the scratch rule: a scratch workspace whose process
    /// is still running keeps its row. It is a real workspace for as long as
    /// it lasts, and a row that appears and disappears on every sweep is a
    /// list the reader cannot trust.
    /// </summary>
    [Fact]
    public async Task PruneMissingWorkspaces_KeepsScratchRow_WhileItsProcessIsAlive()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "eling-prune-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(scratch);

        try
        {
            using var workspaces = new JsonWorkspaceRegistry(Path.Combine(_dir, "agent-workspaces.json"));
            using var registry = new RuntimeRegistry(
                NullLogger<RuntimeRegistry>.Instance,
                UserScope.Resolve(Path.Combine(_dir, "user-scope")),
                new MemoryChangeBroadcaster(),
                workspaces);

            // The runtime owns that scratch folder: it is a workspace while it
            // runs, and the row it records has to survive the sweep.
            registry.Register(Registration(scratch));

            // Act
            await registry.PruneMissingWorkspacesAsync();

            // Assert
            var rows = await workspaces.ListAsync();
            Assert.Single(rows);
            Assert.Equal(scratch, rows[0].WorkspaceRoot);
        }
        finally
        {
            TryDelete(scratch);
        }
    }

    /// <summary>
    /// Liveness is the OS's answer, not the heartbeat's. A heartbeat stays
    /// fresh for the whole stale window, so a scratch row whose process exited
    /// a second ago would still count as live and the list would stay dirty
    /// for minutes after the run that created it.
    /// </summary>
    [Fact]
    public async Task PruneMissingWorkspaces_RemovesScratchRow_WhoseProcessIsGone()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "eling-prune-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(scratch);

        try
        {
            using var workspaces = new JsonWorkspaceRegistry(Path.Combine(_dir, "agent-workspaces.json"));
            using var registry = NewRegistry(workspaces);

            // A process id that cannot exist: the heartbeat is fresh, the
            // process is not there.
            const int deadPid = 999_999_999;
            registry.Register(Registration(scratch, deadPid));

            await registry.PruneMissingWorkspacesAsync();

            var rows = await workspaces.ListAsync();
            Assert.Empty(rows);
        }
        finally
        {
            TryDelete(scratch);
        }
    }

    /// <summary>
    /// A workspace registration refused to record must not be written back by
    /// a heartbeat. Otherwise the throwaway folder's row comes back on every
    /// sweep, right after the prune dropped it, and the list is never clean.
    /// </summary>
    /// <remarks>
    /// The positive control matters: without it the test would pass even if
    /// the heartbeat recorded nothing at all. A heartbeat on an ordinary
    /// workspace has to produce a row; a heartbeat on an excluded one must
    /// not.
    /// </remarks>
    [Fact]
    public async Task Heartbeat_DoesNotReRecordAnExcludedWorkspace()
    {
        // "eling-dash-" is one of the product's own excluded prefixes;
        // "eling-prune-" is not, and is what the control below uses.
        var excluded = Path.Combine(Path.GetTempPath(), "eling-dash-" + Guid.NewGuid().ToString("N")[..8]);
        var ordinary = Path.Combine(_dir, "ordinary");
        Directory.CreateDirectory(excluded);
        Directory.CreateDirectory(ordinary);

        try
        {
            using var workspaces = new JsonWorkspaceRegistry(Path.Combine(_dir, "agent-workspaces.json"));
            using var registry = NewRegistry(workspaces);

            const int pid = 999_999_998;
            registry.Register(Registration(excluded, pid));
            registry.Register(Registration(ordinary, pid));

            // Act
            registry.Heartbeat(pid);

            // Assert - the ordinary workspace was recorded, the excluded one
            // was not.
            var rows = await workspaces.ListAsync();
            Assert.Equal(ordinary, Assert.Single(rows).WorkspaceRoot);
        }
        finally
        {
            TryDelete(excluded);
        }
    }

    /// <summary>
    /// A row that was deleted has to be announced. Without the notification
    /// the row stays on screen until some other event makes the list re-read,
    /// and "the list is clean" is the whole point of pruning it.
    /// </summary>
    [Fact]
    public async Task PruneMissingWorkspaces_NotifiesTheDashboard()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "eling-prune-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(scratch);

        try
        {
            using var workspaces = new JsonWorkspaceRegistry(Path.Combine(_dir, "agent-workspaces.json"));
            var broadcaster = new MemoryChangeBroadcaster();
            using var registry = new RuntimeRegistry(
                NullLogger<RuntimeRegistry>.Instance,
                UserScope.Resolve(Path.Combine(_dir, "user-scope")),
                broadcaster,
                workspaces);

            var now = DateTimeOffset.UtcNow;
            workspaces.Record(new RegisteredWorkspace(string.Empty, scratch, scratch, CodebaseEnabled: true, FirstSeenAt: now, LastSeenAt: now));

            // The subscription has to exist BEFORE the prune runs, or the
            // notification goes to nobody: the iterator registers its channel
            // when it is first advanced, so the advance is started first and
            // awaited afterwards.
            using var cts = new CancellationTokenSource();
            await using var enumerator = broadcaster.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
            var pending = enumerator.MoveNextAsync();

            // Act
            await registry.PruneMissingWorkspacesAsync();

            // Assert - bounded, so a regression fails the test instead of
            // hanging the run.
            Assert.True(await pending.AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("runtimes", enumerator.Current);
        }
        finally
        {
            TryDelete(scratch);
        }
    }

    private RuntimeRegistry NewRegistry(JsonWorkspaceRegistry workspaces) => new(
        NullLogger<RuntimeRegistry>.Instance,
        UserScope.Resolve(Path.Combine(_dir, "user-scope")),
        new MemoryChangeBroadcaster(),
        workspaces);

    /// <summary>
    /// Best-effort removal of a scratch folder. A leftover folder is the
    /// litter this whole change is about, so the fixture does not add to it.
    /// </summary>
    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static RuntimeRegistration Registration(string workspace, int pid = 0) => new()
    {
        ProcessId = pid == 0 ? Environment.ProcessId : pid,
        HeadScopeRoot = workspace,
        WorkspaceRoot = workspace,
        CodebaseEnabled = true,
        DataDirectory = Path.Combine(workspace, ".eling"),
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
