namespace Eling.Core;

/// <summary>
/// Lightweight counters describing the state of the memory store at recall
/// time. Useful for MCP clients to show a "you have N memories" header
/// without enumerating everything.
/// </summary>
public sealed record MemoryRecallStats(
    int TotalMemories,
    int ActiveMemories,
    int ActiveIntentions,
    int RecallCount,
    int RecentCount);
