namespace Eling.Core.Memory;

/// <summary>
/// Result of a summarization: the digest paragraphs plus a label naming what
/// produced them ("judge" or "heuristic") so a consumer can tell a
/// model-authored digest from a deterministic one.
/// </summary>
public sealed record MemorySummary(
    IReadOnlyList<string> Paragraphs,
    string Source);
