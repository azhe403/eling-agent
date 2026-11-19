namespace Eling.Core.Recall;

/// <summary>Result of matching one intention against the current recall context.</summary>
internal sealed record IntentionMatchResult(
    bool Matched,
    bool Expired);
