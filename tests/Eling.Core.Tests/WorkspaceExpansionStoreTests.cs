using Eling.Core.Workspaces;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eling.Core.Tests;

public sealed class WorkspaceExpansionStoreTests
{
    [Fact]
    public void Load_MissingFile_ReturnsEmptyShape()
    {
        using var temp = new TempWorkspace();
        var store = new WorkspaceExpansionStore(Path.Combine(temp.Root, "workspace-expansion.json"));

        var expansion = store.Load();

        Assert.Empty(expansion.Expanded);
        Assert.Empty(expansion.Collapsed);
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsBothLists()
    {
        using var temp = new TempWorkspace();
        var store = new WorkspaceExpansionStore(Path.Combine(temp.Root, "workspace-expansion.json"));

        store.Save(new WorkspaceExpansion(["repo:/repo", "ws:/repo"], ["ws:/other"]));
        var reloaded = new WorkspaceExpansionStore(store.FilePath).Load();

        Assert.Equal(["repo:/repo", "ws:/repo"], reloaded.Expanded);
        Assert.Equal(["ws:/other"], reloaded.Collapsed);
    }

    [Fact]
    public void Save_CreatesMissingDirectories()
    {
        using var temp = new TempWorkspace();
        var store = new WorkspaceExpansionStore(
            Path.Combine(temp.Root, "nested", "deeper", "workspace-expansion.json"));

        store.Save(new WorkspaceExpansion(["ws:/repo"], []));

        Assert.True(File.Exists(store.FilePath));
    }

    [Fact]
    public void Save_DropsDuplicatesAndBlanks()
    {
        using var temp = new TempWorkspace();
        var store = new WorkspaceExpansionStore(Path.Combine(temp.Root, "workspace-expansion.json"));

        store.Save(new WorkspaceExpansion(["ws:/a", "ws:/a", " ", ""], ["ws:/b", "ws:/b"]));
        var reloaded = store.Load();

        Assert.Equal(["ws:/a"], reloaded.Expanded);
        Assert.Equal(["ws:/b"], reloaded.Collapsed);
    }

    [Fact]
    public void Save_ConcurrentWriters_NeverThrowAndLeaveOneWholeDocument()
    {
        using var temp = new TempWorkspace();
        var path = Path.Combine(temp.Root, "workspace-expansion.json");

        // The desktop and the dashboard both write after a toggle. A shared
        // temp file plus a rename is not safe against that on its own: one
        // writer's Move can land while the other still holds the temp file.
        var failures = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        Parallel.For(0, 64, i =>
        {
            try
            {
                var store = new WorkspaceExpansionStore(path);
                store.Save(new WorkspaceExpansion([$"ws:/a{i}", $"ws:/b{i}"], [$"ws:/c{i}"]));
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        });

        Assert.Empty(failures);

        // Whatever won, the file must still be a readable document — never a
        // torn one.
        var reloaded = new WorkspaceExpansionStore(path).Load();
        Assert.True(reloaded.Expanded.Count == 2);
        Assert.Single(reloaded.Collapsed);
    }

    [Fact]
    public void Load_CorruptFile_ReturnsEmptyShapeInsteadOfThrowing()
    {
        using var temp = new TempWorkspace();
        var path = Path.Combine(temp.Root, "workspace-expansion.json");
        File.WriteAllText(path, "{ not json");
        var store = new WorkspaceExpansionStore(path, NullLogger<WorkspaceExpansionStore>.Instance);

        var expansion = store.Load();

        Assert.Empty(expansion.Expanded);
        Assert.Empty(expansion.Collapsed);
    }
}
