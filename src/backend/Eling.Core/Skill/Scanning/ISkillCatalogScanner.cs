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
public sealed record ScanReport(int Discovered, int External, int Missing, int Failed, int Renamed);

/// <summary>
/// Reconciles the skill catalog (<c>{catalogRoot}/skills/*/</c>) with the
/// SQLite index. Folders with valid Eling frontmatter are indexed as
/// <c>eling</c> (upsert by ULID); anything else is indexed as
/// <c>external</c> — a ULID is assigned at scan time but NEVER written back
/// to the file. External files are never read for mutation purposes and
/// never modified. Index rows whose folder vanished are removed. Parse
/// failures are skipped with a warning and tallied in the report; a scan
/// never aborts on a bad folder. Rescanning is idempotent.
/// </summary>
public interface ISkillCatalogScanner
{
    /// <summary>
    /// Walks the catalog, upserts index rows and returns a
    /// <see cref="ScanReport"/>. Missing folders are detected against the
    /// pre-scan active index set (trashed rows are left alone). Never throws
    /// for individual folder failures; only infrastructure failures
    /// (e.g. the index itself is unavailable) propagate.
    /// </summary>
    Task<ScanReport> ScanAsync();
}
