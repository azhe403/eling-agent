namespace Eling.Core;

/// <summary>
/// One intention surfaced during recall, paired with its trigger-match state
/// for the supplied context so MCP clients can decide whether to surface it.
/// </summary>
public sealed record MemoryRecallIntentionResult(
    Intention Intention,
    bool Matched,
    bool Expired);
