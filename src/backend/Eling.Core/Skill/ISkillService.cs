using Eling.Core.Skill.Storage;

namespace Eling.Core.Skill;

/// <summary>
/// Create-or-update-by-name payload for <see cref="ISkillService.SaveAsync"/>.
/// The input is the complete new content of the skill; fields the input cannot
/// express (<see cref="Skill.Source"/>) are preserved from the existing file on
/// update. Sibling files are written verbatim into the folder.
/// </summary>
public sealed record SkillSaveInput(
    string Name,
    string Description,
    string Body,
    IReadOnlyCollection<string>? Tags = null,
    IReadOnlyCollection<RelatedReference>? RelatedMemories = null,
    IReadOnlyCollection<RelatedReference>? RelatedSnippets = null,
    IReadOnlyList<SkillSiblingFile>? SiblingFiles = null);

/// <summary>
/// Application-level orchestration for the machine-global skill catalog
/// (spec §4): validation, ownership enforcement, storage + index coordination.
/// Every mutating path enforces <see cref="EnsureElingOwnedAsync"/> — external
/// skills are read-only forever. Reads are origin-agnostic and serve both
/// Eling-managed and externally-supplied skills.
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
    Task<Skill> SaveAsync(SkillSaveInput input);

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
    /// ("eling"/"external", case-insensitive).
    /// </summary>
    Task<IReadOnlyCollection<SkillSearchResult>> SearchAsync(string query, string? origin = null, int limit = 10);

    /// <summary>
    /// Lists skills ordered by <paramref name="sort"/> ("name" or
    /// "updated_at") with paging applied. Returns the page window, the size of
    /// the whole catalog (<c>Total</c>, including trashed rows) and the count
    /// matching the requested filter (<c>Matching</c>).
    /// </summary>
    Task<(IReadOnlyList<Skill> Items, int Total, int Matching)> ListAsync(
        bool includeTrashed, string? origin, int page = 1, int pageSize = 20, string sort = "name");

    /// <summary>
    /// Moves an Eling-managed skill to the catalog-local trash and marks the
    /// index row trashed (kept in the index, excluded from list/search by
    /// default). Refuses external skills (<see cref="SkillReadOnlyException"/>).
    /// </summary>
    Task DeleteAsync(string name);

    /// <summary>
    /// Restores a trashed skill to the active catalog and re-indexes it,
    /// clearing the trash marker. Works even when the index row is missing
    /// (e.g. after <see cref="RebuildIndexAsync"/> dropped it) — the folder is
    /// the authority for the restore. Refuses when the name is externally
    /// owned (<see cref="SkillReadOnlyException"/>); throws
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
    /// are dropped entirely (their folders stay in the trash). Trashed skills
    /// therefore disappear from list/search until restored; <see cref="RestoreAsync"/>
    /// re-indexes from disk and clears the marker, which is the ordering this
    /// method relies on. The implementation re-indexes externally-owned rows
    /// whose folder still exists so recall/search keeps covering both origins
    /// between a rebuild and the next catalog scan.
    /// </remarks>
    Task RebuildIndexAsync();

    /// <summary>
    /// Throws <see cref="SkillNotFoundException"/> when the name is absent from
    /// the index, <see cref="SkillReadOnlyException"/> when it is owned by an
    /// external tool, and returns normally for Eling-managed skills. Called
    /// before every mutating operation.
    /// </summary>
    Task EnsureElingOwnedAsync(string name);
}
