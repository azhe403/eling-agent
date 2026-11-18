namespace Eling.Core.Skill;

/// <summary>
/// Application-level orchestration for the machine-global skill catalog
/// (spec §4): validation, ownership enforcement, storage + index coordination.
/// Content-mutating paths (save, rename) refuse external skills — they are
/// never written, renamed or overwritten. Delete-to-trash (restorable) and
/// the explicit user-called adoption (<see cref="AdoptAsync"/>) are the two
/// exceptions; reads are origin-agnostic and serve both Eling-managed and
/// externally-supplied skills.
/// </summary>
/// <remarks>
/// Read fidelity: reads are served from the index mirror, where
/// <see cref="Skill.Source"/> is not round-tripped (always <c>null</c>) and a
/// trashed row is excluded via the active-catalog check. <see cref="GetByNameAsync"/>
/// additionally re-reads the file for Eling-origin skills when it parses
/// cleanly, so Source is preserved on get. The FTS search path is
/// origin-agnostic and always excludes trashed rows.
/// </remarks>
public interface ISkillService
{
    /// <summary>
    /// Creates a new skill, or replaces the content of an existing
    /// Eling-managed skill with the same name. The ULID and <c>CreatedAt</c>
    /// are preserved across updates. Rejects a name held by an externally-owned
    /// skill (<see cref="SkillReadOnlyException"/>) and a name held by an
    /// unscanned folder (<see cref="Storage.SkillConflictException"/>).
    /// Updating a skill whose name is occupied in the trash is refused by
    /// storage (<see cref="Storage.SkillConflictException"/>).
    /// </summary>
    /// <remarks>
    /// The result's <see cref="SkillSaveResult.Action"/> reports
    /// <see cref="SkillSaveAction.Created"/> when no index row existed for the
    /// name, <see cref="SkillSaveAction.Updated"/> otherwise — a drifted-but-present
    /// row (file unreadable, index mirror used) counts as Updated.
    /// </remarks>
    Task<SkillSaveResult> SaveAsync(SkillSaveInput input);

    /// <summary>
    /// Renames an Eling-managed skill, moving the folder and rewriting the
    /// <c>name</c> key in SKILL.md atomically; the ULID (and every other field)
    /// is preserved. The new name must be kebab-case and unoccupied (active or
    /// trashed). Throws <see cref="SkillReadOnlyException"/> when the target
    /// name is externally owned, <see cref="Storage.SkillConflictException"/>
    /// when it is otherwise occupied.
    /// </summary>
    Task<Skill> RenameAsync(string oldName, string newName);

    /// <summary>
    /// The active skill named <paramref name="name"/>, or <c>null</c> when the
    /// name is unknown or the skill is in the trash. For Eling-origin skills
    /// the file is re-read when it parses, preserving <see cref="Skill.Source"/>;
    /// an unparsable folder reads as <c>null</c>. External skills are served
    /// from the index mirror (Source is not round-tripped there).
    /// </summary>
    Task<Skill?> GetByNameAsync(string name);

    /// <summary>
    /// The active skill with <paramref name="id"/>, or <c>null</c>. Matched by
    /// scanning the active index set — O(catalog size); resolve by name when
    /// the name is known. Works for both origins (external ids live only in
    /// the index).
    /// </summary>
    Task<Skill?> GetByIdAsync(SkillId id);

    /// <summary>
    /// BM25 search over name/description/body (both origins). Trashed rows are
    /// always excluded. <paramref name="origin"/> filters by wire name
    /// ("eling"/"external", case-insensitive). <paramref name="tag"/> filters
    /// by tag in the index query itself (trimmed, case-insensitive) rather
    /// than post-filtering hydrated rows.
    /// </summary>
    Task<IReadOnlyCollection<SkillSearchResult>> SearchAsync(string query, string? origin = null, int limit = 10, string? tag = null);

    /// <summary>
    /// Lists skills ordered by <paramref name="sort"/> ("name" or
    /// "updated_at") with paging applied. Returns a <see cref="SkillListResult"/>:
    /// the page window <see cref="SkillListResult.Items"/>, the size of the
    /// request's selection — the includeTrashed flag is honored, the origin
    /// filter is ignored (<see cref="SkillListResult.Total"/>), and the count
    /// matching the requested filter before the window was applied
    /// (<see cref="SkillListResult.Matching"/>). With no filters applied,
    /// Total == Matching.
    /// </summary>
    Task<SkillListResult> ListAsync(
        bool includeTrashed, string? origin, int page = 1, int pageSize = 20, string sort = "name");

    /// <summary>
    /// Moves a skill to the catalog-local trash and marks the index row
    /// trashed (kept in the index, excluded from list/search by default).
    /// Works for both origins — external skills are moved, never edited, and
    /// the move is restorable via <see cref="RestoreAsync"/>. Throws
    /// <see cref="SkillNotFoundException"/> when the name is unknown.
    /// </summary>
    Task DeleteAsync(string name);

    /// <summary>
    /// Restores a trashed skill to the active catalog and re-indexes it,
    /// clearing the trash marker. Works even after <see cref="RebuildIndexAsync"/>
    /// dropped the row (the folder is the authority for the restore). Eling-origin
    /// rows are re-indexed from disk; external rows are re-activated from the
    /// preserved index mirror (their files are never parsed or rewritten). Throws
    /// <see cref="SkillNotFoundException"/> when no trashed folder exists.
    /// </summary>
    Task RestoreAsync(string name);

    /// <summary>
    /// Re-indexes the catalog from scratch: active skills are read from the
    /// storage list, and externally-owned rows whose folder still exists are
    /// carried over so search/list continuity holds across a rebuild.
    /// </summary>
    /// <remarks>
    /// <see cref="ISkillIndex.RebuildAsync"/> drops every row and re-inserts;
    /// the trash marker is not part of <see cref="Skill"/>, so trashed rows
    /// are carried verbatim (body_path and trashed_at preserved). Active
    /// external rows whose folder still exists also survive verbatim, matched
    /// against their stored body_path rather than a display name — that is
    /// the only difference an external row sees. A name reclaimed by a stored
    /// Eling folder wins; the index mirror may lag until the next rescan.
    /// </remarks>
    Task RebuildIndexAsync();

    /// <summary>
    /// Explicit, user-requested <b>adoption</b>: converts an externally-owned
    /// skill to an Eling-managed one, in place. The folder and every sibling
    /// file stay untouched; only SKILL.md's frontmatter is rewritten (keys
    /// updated/added: <c>id</c>, <c>createdAt</c>, <c>updatedAt</c>; everything
    /// else preserved verbatim, body byte-for-byte), and the index row flips
    /// to <see cref="SkillOrigin.Eling"/> keeping its identity. The ULID is
    /// reused from the index row when present. Refuses — without touching
    /// anything — when the name is unknown (<see cref="SkillNotFoundException"/>),
    /// the skill is already Eling-managed (<see cref="Storage.SkillConflictException"/>),
    /// the skill is trashed (<see cref="SkillValidationException"/>), the
    /// frontmatter does not parse (<see cref="SkillValidationException"/> with
    /// the parse failure and the instruction to fix the YAML first), or the
    /// frontmatter <c>name</c> disagrees with the folder name. When no index
    /// row exists but the folder is present (legacy state), a new row is
    /// inserted as <see cref="SkillOrigin.Eling"/>.
    /// </summary>
    Task<AdoptResult> AdoptAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Throws <see cref="SkillNotFoundException"/> when the name is absent from
    /// the index, <see cref="SkillReadOnlyException"/> when it is owned by an
    /// external tool, and returns normally for Eling-managed skills. Called
    /// before every content-mutating operation (save, rename); delete-to-trash
    /// is deliberately not gated on it.
    /// </summary>
    Task EnsureElingOwnedAsync(string name);
}
