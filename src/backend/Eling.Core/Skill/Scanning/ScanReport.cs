namespace Eling.Core.Skill.Scanning;

/// <summary>
/// Outcome of one catalog reconciliation scan. All counts describe what
/// changed during <see cref="ISkillCatalogScanner.ScanAsync"/>, so a repeated
/// scan over an unchanged catalog reports zero changes.
/// </summary>
/// <param name="Discovered">Eling-origin skills newly added to the index (by id).</param>
/// <param name="External">Externally-supplied skills newly adopted into the index.</param>
/// <param name="Missing">Active index rows removed because their folder vanished.</param>
/// <param name="Failed">Folders that could not be indexed (bad YAML, unreadable file, broken Eling frontmatter, invalid folder name); skipped with a warning.</param>
/// <param name="Renamed">Eling-origin skills whose index row was re-keyed to a new folder/frontmatter name (matched by stable ULID).</param>
public sealed record ScanReport(
    int Discovered,
    int External,
    int Missing,
    int Failed,
    int Renamed);
