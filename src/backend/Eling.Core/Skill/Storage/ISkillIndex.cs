namespace Eling.Core.Skill.Storage;

/// <summary>
/// SQLite FTS5 index for the skill catalog, backed by a dedicated
/// <c>skill-index.db</c> (never the memories/snippets index). Sync between
/// <c>skills</c> and <c>skills_fts</c> is manual — delete + insert FTS rows
/// inside the same transaction as the relational upsert — which supersedes
/// the trigger-based wording of the design spec (mirrors
/// <c>SqliteMemoryIndex</c>).
/// </summary>
public interface ISkillIndex
{
    /// <summary>
    /// Upserts an Eling-managed skill by id (insert-or-replace). FTS rows are
    /// deleted and re-inserted in the same transaction. Refuses
    /// <see cref="SkillOrigin.External"/> skills — <see cref="AdoptExternalScanResultAsync"/>
    /// is the only index path for external rows.
    /// </summary>
    Task IndexAsync(Skill skill);

    /// <summary>Removes the skill row (cascading tags/relations and the FTS row) by name. No-op when absent.</summary>
    Task RemoveAsync(string name);

    /// <summary>
    /// Full re-index from a list: drops every row and re-inserts, recreating
    /// FTS state from scratch. Each skill's <see cref="Skill.Origin"/> is
    /// preserved as passed — the bulk path is origin-agnostic (the scanner
    /// rebuilds a mixed catalog in one go), while <see cref="IndexAsync"/>
    /// stays the guarded per-skill path for Eling-managed rows and
    /// <see cref="AdoptExternalScanResultAsync"/> the scanner's per-row path.
    /// Trash state is not part of <see cref="Skill"/> and is therefore not
    /// preserved by a rebuild — callers that need it must re-apply
    /// <see cref="MarkTrashedAsync"/>.
    /// </summary>
    Task RebuildAsync(IEnumerable<Skill> skills);

    /// <summary>Returns the skill stored under <paramref name="name"/>, or <c>null</c>.</summary>
    Task<Skill?> GetByNameAsync(string name);

    /// <summary>
    /// BM25 search over name/description/body with porter-stemmed ranking
    /// (×1.0). Trashed rows are excluded unless <paramref name="includeTrashed"/>.
    /// <paramref name="origin"/> filters by wire name ("eling"/"external", case-insensitive).
    /// </summary>
    Task<IReadOnlyCollection<SkillSearchResult>> SearchAsync(string query, string? origin = null, int limit = 10, bool includeTrashed = false);

    /// <summary>Lists skills ordered by <paramref name="sort"/> ("name" or "updated_at"). Trashed rows are included only when <paramref name="includeTrashed"/>.</summary>
    Task<IReadOnlyList<Skill>> ListAsync(bool includeTrashed, string? origin, string sort = "name");

    /// <summary>Sets or clears the <c>trashed_at</c> marker. Throws when the name is not in the index.</summary>
    Task MarkTrashedAsync(string name, bool trashed);

    /// <summary>
    /// The only index path for external rows: upserts <paramref name="skill"/>
    /// with <c>origin='external'</c> regardless of the value on the aggregate.
    /// Used by the catalog scanner; never writes to the file.
    /// </summary>
    Task AdoptExternalScanResultAsync(Skill skill);

    /// <summary>True when any row (including trashed) holds <paramref name="name"/>.</summary>
    Task<bool> IsNameTakenAsync(string name);
}
