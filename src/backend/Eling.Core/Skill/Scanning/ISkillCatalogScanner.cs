namespace Eling.Core.Skill.Scanning;

/// <summary>
/// Reconciles the skill catalog (<c>{catalogRoot}/skills/*/</c>) with the
/// SQLite index. Folders with valid Eling frontmatter are indexed as
/// <c>eling</c> (upsert by ULID); anything else is indexed as
/// <c>external</c> - a ULID is assigned at scan time but NEVER written back
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
