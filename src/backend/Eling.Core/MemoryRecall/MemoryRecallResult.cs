using Eling.Core.Memory;

namespace Eling.Core.MemoryRecall;

/// <summary>
/// Result of a memory recall. <see cref="RecallMemories"/> is derived from
/// topic-based search (so each entry is semantically relevant to the supplied
/// topics) and carries the matched-layer metadata. <see cref="RecentMemories"/>
/// is the most recently updated active memories across the requested scope,
/// useful for surfacing new writes performed by other agents.
/// <see cref="StandingMemories"/> is the topic-independent baseline: standing
/// rules and preferences, standing decisions, and lessons that must stay
/// consistent in every conversation, returned even when the topic call is
/// narrow or matches nothing. <see cref="OverflowMemories"/> holds the relevant
/// hits beyond <c>recallLimit</c> that would otherwise be dropped; they are
/// meant to be summarized, not skipped.
/// </summary>
public sealed record MemoryRecallResult(
    IReadOnlyList<MemoryRecallHit> RecallMemories,
    IReadOnlyList<ScopedMemory> RecentMemories,
    IReadOnlyList<MemoryRecallIntentionResult> Intentions,
    MemoryRecallStats Stats,
    IReadOnlyList<MemoryRecallHit>? StandingMemories = null,
    IReadOnlyList<MemoryRecallHit>? OverflowMemories = null);
