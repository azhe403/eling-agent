namespace Eling.Core.Tests;

/// <summary>
/// Runs the worktree-index policy tests on their own, never alongside another
/// collection.
/// </summary>
/// <remarks>
/// The suite's timing-sensitive audit measurements — how many of 4128 entries a
/// backed-up channel drain lands inside its budget — depend on the machine
/// being quiet, and they fail when anything else runs at the same time. A new
/// test class is exactly such a neighbour, so this one is kept out of the
/// parallel phase instead of making an unrelated measurement carry the cost.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WorktreeIndexCollection
{
    public const string Name = "WorktreeIndex";
}
