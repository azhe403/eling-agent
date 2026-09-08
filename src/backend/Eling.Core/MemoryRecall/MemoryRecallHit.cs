namespace Eling.Core;

/// <summary>
/// A recalled memory with the search-layer metadata that produced it:
/// which FTS layers matched (<c>porter</c> / <c>trigram</c>), the
/// per-layer BM25-derived scores, and the query mode ("and" or
/// "or-fallback") that produced the hit. Populated by the recall
/// service so the MCP response can show why a memory surfaced for the
/// given topics and which query strategy was used.
/// </summary>
public sealed record MemoryRecallHit(
    Memory Memory,
    IReadOnlyCollection<string>? MatchedVia = null,
    double PorterScore = 0.0,
    double TrigramScore = 0.0,
    string? QueryMode = null);
