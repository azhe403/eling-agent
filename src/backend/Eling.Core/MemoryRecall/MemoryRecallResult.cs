namespace Eling.Core;

/// <summary>
/// Result of a memory recall. <see cref="RecallMemories"/> is derived from
/// topic-based search (so each entry is semantically relevant to the supplied
/// topics). <see cref="RecentMemories"/> is the most recently updated active
/// memories across the requested scope, useful for surfacing new writes
/// performed by other agents.
/// </summary>
public sealed record MemoryRecallResult(
    IReadOnlyList<Memory> RecallMemories,
    IReadOnlyList<Memory> RecentMemories,
    IReadOnlyList<MemoryRecallIntentionResult> Intentions,
    MemoryRecallStats Stats);
