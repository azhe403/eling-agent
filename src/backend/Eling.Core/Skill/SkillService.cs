using Eling.Core.Skill.Storage;
using Microsoft.Extensions.Logging;

namespace Eling.Core.Skill;

/// <summary>
/// Default <see cref="ISkillService"/>. Coordinates the file store
/// (<see cref="ISkillStorage"/>) and the FTS index (<see cref="ISkillIndex"/>)
/// under one ownership rule: every mutating path goes through
/// <see cref="EnsureElingOwnedAsync"/> (or an equivalent origin check on the
/// target name) first — external skills are read-only forever.
/// </summary>
/// <remarks>
/// Orchestration order: validate input → collision guard (index lookup by
/// name; external wins) → storage write → index update. Writes are not
/// transactional across storage and index; storage is the source of truth and
/// is always written first, so a crash mid-flow leaves the index stale rather
/// than the file gone (fix with <see cref="RebuildIndexAsync"/> or a rescan).
/// </remarks>
public sealed class SkillService : ISkillService
{
    private readonly ISkillStorage _storage;
    private readonly ISkillIndex _index;
    private readonly ISkillChangeNotifier _notifier;
    private readonly ILogger<SkillService>? _logger;

    public SkillService(ISkillStorage storage, ISkillIndex index, ISkillChangeNotifier? notifier = null, ILogger<SkillService>? logger = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _notifier = notifier ?? NullSkillChangeNotifier.Instance;
        _logger = logger;
    }

    public async Task<Skill> SaveAsync(SkillSaveInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateSaveInput(input);

        // Collision guard: a name already held by an externally-owned row is
        // never ours to write. An index row means the folder is tracked; no
        // row but a folder on disk means the catalog has not been scanned
        // yet — writing would clobber content we cannot classify, so refuse
        // until a scan indexes it (as external) or the folder is gone.
        var row = await _index.GetByNameAsync(input.Name);
        if (row is { Origin: SkillOrigin.External })
        {
            throw new SkillReadOnlyException(
                $"Skill '{input.Name}' is externally supplied (owned by its creator's tooling); Eling never writes external skills.");
        }

        if (row is null && await _storage.ExistsAsync(input.Name))
        {
            throw new SkillConflictException(
                $"A folder named '{input.Name}' exists in the catalog but is not indexed. Run a catalog rescan so Eling can classify it before saving.");
        }

        // Update path: the input cannot express Skill.Source, so the existing
        // aggregate (file when readable, index mirror otherwise) carries the
        // fields that survive the rewrite.
        var existing = row is null ? null : await TryGetExistingAsync(input.Name);
        var skill = NewAggregate(
            input,
            id: existing?.Id,
            createdAt: existing?.CreatedAt,
            updatedAt: existing is null ? (DateTimeOffset?)null : DateTimeOffset.UtcNow,
            source: existing?.Source);

        await _storage.SaveAsync(skill, input.SiblingFiles);
        try
        {
            await _index.IndexAsync(skill);
        }
        catch (ArgumentException ex)
        {
            // Vocabulary/scope violations surface from the indexer; report them
            // as validation, not as an internal error.
            throw new SkillValidationException(ex.Message, ex);
        }

        await NotifyAsync("saved");
        return skill;
    }

    public async Task<Skill> RenameAsync(string oldName, string newName)
    {
        ValidateNameOrThrow(oldName, nameof(oldName));
        ValidateNameOrThrow(newName, nameof(newName));

        await EnsureElingOwnedAsync(oldName);

        var target = await _index.GetByNameAsync(newName);
        if (target is { Origin: SkillOrigin.External })
        {
            throw new SkillReadOnlyException(
                $"Skill '{newName}' is externally supplied (owned by its creator's tooling); Eling never writes external skills.");
        }

        if (target is not null)
        {
            throw new SkillConflictException($"A skill named '{newName}' already exists; refusing to overwrite.");
        }

        try
        {
            await _storage.RenameAsync(oldName, newName);
        }
        catch (SkillConflictException)
        {
            throw;
        }
        catch (SkillStorageException ex)
        {
            if (!await _storage.ExistsAsync(oldName))
            {
                throw new SkillNotFoundException($"No active skill named '{oldName}' exists to rename.", ex);
            }

            throw;
        }

        var renamed = await _storage.GetByNameAsync(newName)
            ?? throw new SkillStorageException($"Skill '{newName}' vanished after rename.");
        try
        {
            // The upsert removes the pre-rename row (matched by id) and inserts
            // under the new name; createdAt is preserved via the id lookup
            // inside the index.
            await _index.IndexAsync(renamed);
        }
        catch (ArgumentException ex)
        {
            throw new SkillValidationException(ex.Message, ex);
        }

        await NotifyAsync("renamed");
        return renamed;
    }

    public async Task<Skill?> GetByNameAsync(string name)
    {
        ValidateNameOrThrow(name, nameof(name));

        var row = await _index.GetByNameAsync(name);
        if (row is null)
        {
            return null;
        }

        if (row.Origin == SkillOrigin.Eling)
        {
            try
            {
                // Trashed/missing folders read as null — the active view must
                // not serve them. A readable file wins (full fidelity, incl.
                // Source, ULID matching the index row).
                return await _storage.GetByNameAsync(name);
            }
            catch (SkillStorageException)
            {
                // A drifted-but-present folder still reads from the index
                // mirror: serving last-good content beats a 404 for the agent.
                return row;
            }
        }

        // External: the file is not ours to parse — serve the index mirror
        // while the folder still exists; a vanished folder reads as absent.
        return await _storage.ExistsAsync(name) ? row : null;
    }

    public async Task<Skill?> GetByIdAsync(SkillId id)
    {
        // The index mirror is authoritative for identity; active rows only.
        // O(catalog size) — resolve by name when it is known.
        var active = await _index.ListAsync(includeTrashed: false, origin: null);
        return active.FirstOrDefault(s => s.Id == id);
    }

    public async Task<IReadOnlyCollection<SkillSearchResult>> SearchAsync(string query, string? origin = null, int limit = 10)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new SkillValidationException("Search query must not be empty.");
        }

        if (limit < 1)
        {
            throw new SkillValidationException("Limit must be at least 1.");
        }

        try
        {
            return await _index.SearchAsync(query, origin, limit);
        }
        catch (ArgumentException ex)
        {
            throw new SkillValidationException(ex.Message, ex);
        }
    }

    public async Task<SkillListResult> ListAsync(
        bool includeTrashed, string? origin, int page = 1, int pageSize = 20, string sort = "name")
    {
        if (page < 1)
        {
            throw new SkillValidationException("Page must be at least 1.");
        }

        if (pageSize < 1)
        {
            throw new SkillValidationException("PageSize must be at least 1.");
        }

        IReadOnlyList<Skill> matching;
        try
        {
            matching = await _index.ListAsync(includeTrashed, origin, sort);
        }
        catch (ArgumentException ex)
        {
            throw new SkillValidationException(ex.Message, ex);
        }

        // Total = the whole catalog incl. trashed rows, ignoring the origin
        // filter (the "selection" the page is drawn from); Matching = rows
        // passing the request's filter, before the window is applied.
        var total = (await _index.ListAsync(includeTrashed: true, origin: null)).Count;
        var offset = (page - 1) * pageSize;
        List<Skill> items = matching.Skip(offset).Take(pageSize).ToList();
        return new SkillListResult(items, total, matching.Count);
    }

    public async Task DeleteAsync(string name)
    {
        ValidateNameOrThrow(name, nameof(name));

        await EnsureElingOwnedAsync(name);

        try
        {
            await _storage.MoveToTrashAsync(name);
        }
        catch (SkillConflictException)
        {
            // The trash destination is occupied — the caller must restore or
            // purge that name first; never merge.
            throw;
        }
        catch (SkillStorageException ex)
        {
            // Index row exists but the folder is gone (drift): nothing to move.
            throw new SkillNotFoundException(ex.Message, ex);
        }

        // The row deliberately stays in the index, marked trashed; list/search
        // exclude it by default until restored.
        await _index.MarkTrashedAsync(name, trashed: true);
        await NotifyAsync("deleted");
    }

    public async Task RestoreAsync(string name)
    {
        ValidateNameOrThrow(name, nameof(name));

        var row = await _index.GetByNameAsync(name);
        if (row is { Origin: SkillOrigin.External })
        {
            throw new SkillReadOnlyException(
                $"Skill '{name}' is externally supplied (owned by its creator's tooling); Eling never mutates external skills.");
        }

        try
        {
            await _storage.RestoreAsync(name);
        }
        catch (SkillConflictException)
        {
            // An active skill with the same name exists; restore must not
            // overwrite it.
            throw;
        }
        catch (SkillStorageException ex)
        {
            throw new SkillNotFoundException($"No trashed skill named '{name}' exists to restore.", ex);
        }

        var restored = await _storage.GetByNameAsync(name)
            ?? throw new SkillStorageException($"Skill '{name}' vanished after restore.");
        try
        {
            // IndexAsync re-applies the preserved trashed_at of any row that
            // already held the id, so the re-indexed skill could still read as
            // trashed — clear the marker explicitly afterwards.
            await _index.IndexAsync(restored);
        }
        catch (ArgumentException ex)
        {
            throw new SkillValidationException(ex.Message, ex);
        }

        await _index.MarkTrashedAsync(name, trashed: false);
        await NotifyAsync("restored");
    }

    public async Task RebuildIndexAsync()
    {
        // RebuildAsync wipes the index wholesale: rows for trashed folders are
        // dropped (trashed skills are not in the active storage list and the
        // trash marker is not part of the aggregate). Trashed skills therefore
        // disappear from list/search until restored — RestoreAsync re-indexes
        // from disk and clears the marker, which is the ordering restore relies
        // on. Externally-owned rows whose folder still exists are carried over
        // so recall/search keeps covering both origins between a rebuild and
        // the next catalog scan; storage (200-Eling-skills) wins any name clash.
        var storageSkills = await _storage.ListAllAsync();
        var storageNames = new HashSet<string>(storageSkills.Select(s => s.Name), StringComparer.Ordinal);

        var kept = new List<Skill>(storageSkills);
        foreach (var row in await _index.ListAsync(includeTrashed: true, origin: null))
        {
            if (storageNames.Contains(row.Name))
            {
                continue;
            }

            if (row.Origin == SkillOrigin.External && await _storage.ExistsAsync(row.Name))
            {
                kept.Add(row);
            }
        }

        await _index.RebuildAsync(kept);
        await NotifyAsync("rebuilt");
    }

    public async Task EnsureElingOwnedAsync(string name)
    {
        ValidateNameOrThrow(name, nameof(name));

        var row = await _index.GetByNameAsync(name);
        if (row is null)
        {
            throw new SkillNotFoundException($"No skill named '{name}' exists in the skill catalog.");
        }

        if (row.Origin == SkillOrigin.External)
        {
            throw new SkillReadOnlyException(
                $"Skill '{name}' is externally supplied (owned by its creator's tooling); Eling never mutates external skills.");
        }
    }

    private async Task<Skill?> TryGetExistingAsync(string name)
    {
        try
        {
            var stored = await _storage.GetByNameAsync(name);
            if (stored is not null)
            {
                return stored;
            }
        }
        catch (SkillStorageException ex)
        {
            // Drifted file: fall back to the index mirror - a failed read must
            // not turn an update into data loss.
            _logger?.LogWarning(ex, "Skill file unparsable while reading '{Name}'; falling back to the index mirror.", name);
        }

        return await _index.GetByNameAsync(name);
    }

    private static Skill NewAggregate(
        SkillSaveInput input,
        SkillId? id,
        DateTimeOffset? createdAt,
        DateTimeOffset? updatedAt,
        SkillSource? source)
    {
        try
        {
            return new Skill(
                input.Name,
                input.Description,
                input.Body,
                input.Tags,
                input.RelatedMemories,
                input.RelatedSnippets,
                source,
                id,
                SkillOrigin.Eling,
                createdAt,
                updatedAt);
        }
        catch (ArgumentException ex)
        {
            throw new SkillValidationException(ex.Message, ex);
        }
    }

    private static void ValidateNameOrThrow(string name, string paramName)
    {
        if (!Skill.ValidateName(name))
        {
            throw new SkillValidationException(
                $"{paramName}: skill name must be lowercase kebab-case (^[a-z0-9]+(-[a-z0-9]+)*$) and at most {Skill.MaxNameLength} characters.");
        }
    }

    private static void ValidateSaveInput(SkillSaveInput input)
    {
        ValidateNameOrThrow(input.Name, nameof(input));

        if (string.IsNullOrWhiteSpace(input.Description))
        {
            throw new SkillValidationException("Description must not be empty.");
        }

        if (input.Description.Length > Skill.MaxDescriptionLength)
        {
            throw new SkillValidationException($"Description must be at most {Skill.MaxDescriptionLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(input.Body))
        {
            throw new SkillValidationException("Body must not be empty.");
        }
    }

    private async Task NotifyAsync(string reason)
    {
        try
        {
            await _notifier.NotifyAsync(reason);
        }
        catch (Exception ex)
        {
            // Notifications are best-effort and fire after the mutation has
            // landed; a refresh failure must not turn a successful write into
            // an error.
            _logger?.LogWarning(ex, "Skill change notification '{Reason}' failed; the mutation itself landed.", reason);
        }
    }
}
