namespace Eling.Core.Skill.Scanning;

/// <summary>
/// One skill folder the catalog scan could not index. Collected on the
/// <see cref="ScanReport"/> so callers (REST rescan, dashboard) can surface
/// the actual reason instead of just a count.
/// </summary>
/// <param name="Path">Absolute path of the offending skill folder.</param>
/// <param name="Name">Folder name, when known.</param>
/// <param name="Reason">Short human-readable reason — the parse exception's
/// message when a parse threw, or a short fixed reason otherwise (e.g.
/// "kebab-case violation", "SKILL.md is missing").</param>
public sealed record SkillScanFailure(
    string Path,
    string? Name,
    string Reason);
