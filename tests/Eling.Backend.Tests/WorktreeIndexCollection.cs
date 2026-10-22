namespace Eling.Backend.Tests;

/// <summary>
/// Runs the worktree-index inheritance tests on their own, never alongside
/// another collection.
/// </summary>
/// <remarks>
/// These tests start a real file watcher and index real files, and they move
/// the process-global <c>ELING_DATA_DIR</c> to keep their databases out of the
/// developer's store. Both reasons ask for isolation: the first because the
/// suite's timing-sensitive audit measurements fail when the machine is busy,
/// the second because that variable is process-wide. Running the class alone
/// answers both at once.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WorktreeIndexCollection
{
    public const string Name = "WorktreeIndex";
}
