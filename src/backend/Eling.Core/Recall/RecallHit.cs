using Eling.Core.Memory;
using Eling.Core.Skill;

namespace Eling.Core.Recall;

/// <summary>
/// A recalled memory with the search-layer metadata that produced it:
/// which FTS layers matched (<c>porter</c> / <c>trigram</c>), the
/// per-layer BM25-derived scores, and the query mode ("and" or
/// "or-fallback") that produced the hit. Populated by the recall
/// service so the MCP response can show why a memory surfaced for the
/// given topics and which query strategy was used. Carries scope
/// provenance: which chain level (or global) the hit came from.
/// </summary>
public sealed record RecallHit(
    Memory.Memory Memory,
    IReadOnlyCollection<string>? MatchedVia = null,
    double PorterScore = 0.0,
    double TrigramScore = 0.0,
    string? QueryMode = null,
    MemoryScopeKind Scope = MemoryScopeKind.Project,
    string? ProjectRoot = null);

/// <summary>
/// A skill referenced by recall output: name, one-line description,
/// origin ("eling"/"external"), and the absolute SKILL.md path. Bodies
/// are never inlined - loading the skill is a separate step (read the
/// path), matching the snippet philosophy: recall surfaces what exists
/// and where, never the content itself.
/// </summary>
public sealed record SkillRecallHit(
    string Name,
    string Description,
    SkillOrigin Origin,
    string Path);
