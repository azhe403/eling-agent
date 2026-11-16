using Eling.Core.Skill.Serialization;

namespace Eling.Core.Skill.Storage;

/// <summary>
/// Test/double implementation of <see cref="ISkillStorage"/>. Mirrors the file
/// storage's contract exactly: same name validation, same conflict rules
/// (trash destinations never merge, restore refuses to overwrite, saves are
/// blocked by a trashed name), same deletion semantics (permanent delete is
/// trash-only). No files are touched.
/// </summary>
public class InMemorySkillStorage : ISkillStorage
{
    private readonly Dictionary<string, Skill> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Skill> _trash = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<SkillSiblingFile>> _siblings = new(StringComparer.Ordinal);

    public Task SaveAsync(Skill skill, IReadOnlyList<SkillSiblingFile>? siblingFiles = null)
    {
        ArgumentNullException.ThrowIfNull(skill);
        SkillGuards.ValidateNameOrThrow(skill.Name, nameof(skill));

        if (skill.Origin == SkillOrigin.External)
        {
            throw new SkillStorageException(
                $"Skill '{skill.Name}' is externally originated; Eling never writes external skills.");
        }

        if (_trash.ContainsKey(skill.Name))
        {
            throw new SkillConflictException(
                $"A trashed skill named '{skill.Name}' occupies the name. Restore it or delete it permanently before saving a new skill with this name.");
        }

        _active[skill.Name] = skill;
        _siblings[skill.Name] = siblingFiles is null ? [] : [.. siblingFiles];
        return Task.CompletedTask;
    }

    public Task<Skill?> GetByNameAsync(string name)
    {
        SkillGuards.ValidateNameOrThrow(name, nameof(name));
        return Task.FromResult(_active.TryGetValue(name, out var skill) ? skill : null);
    }

    public Task<IReadOnlyList<Skill>> ListAllAsync() =>
        Task.FromResult<IReadOnlyList<Skill>>(
            _active.Values.OrderBy(s => s.Name, StringComparer.Ordinal).ToList());

    public Task RenameAsync(string oldName, string newName)
    {
        SkillGuards.ValidateNameOrThrow(oldName, nameof(oldName));
        SkillGuards.ValidateNameOrThrow(newName, nameof(newName));

        if (string.Equals(oldName, newName, StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        if (!_active.TryGetValue(oldName, out var existing))
        {
            throw new SkillStorageException($"No skill named '{oldName}' exists to rename.");
        }

        if (_active.ContainsKey(newName))
        {
            throw new SkillConflictException($"A skill named '{newName}' already exists; refusing to overwrite.");
        }

        if (_trash.ContainsKey(newName))
        {
            throw new SkillConflictException(
                $"A trashed skill named '{newName}' occupies the name. Restore it or delete it permanently before reusing this name.");
        }

        _active.Remove(oldName);
        _active[newName] = new Skill(
            newName,
            existing.Description,
            existing.Body,
            existing.Tags,
            existing.RelatedMemories,
            existing.RelatedSnippets,
            existing.Source,
            existing.Id,
            SkillOrigin.Eling,
            existing.CreatedAt,
            existing.UpdatedAt);

        if (_siblings.Remove(oldName, out var siblings))
        {
            _siblings[newName] = siblings;
        }

        return Task.CompletedTask;
    }

    public Task MoveToTrashAsync(string name)
    {
        SkillGuards.ValidateNameOrThrow(name, nameof(name));

        if (!_active.TryGetValue(name, out var skill))
        {
            throw new SkillStorageException($"No skill named '{name}' exists to move to trash.");
        }

        if (_trash.ContainsKey(name))
        {
            throw new SkillConflictException(
                $"The trash already contains a skill named '{name}' (a different, earlier-trashed skill). Restore or delete it permanently first; refusing to merge.");
        }

        _active.Remove(name);
        _trash[name] = skill;
        return Task.CompletedTask;
    }

    public Task RestoreAsync(string name)
    {
        SkillGuards.ValidateNameOrThrow(name, nameof(name));

        if (!_trash.TryGetValue(name, out var skill))
        {
            throw new SkillStorageException($"No trashed skill named '{name}' exists to restore.");
        }

        if (_active.ContainsKey(name))
        {
            throw new SkillConflictException($"An active skill named '{name}' already exists; refusing to overwrite.");
        }

        _trash.Remove(name);
        _active[name] = skill;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string name)
    {
        SkillGuards.ValidateNameOrThrow(name, nameof(name));

        if (!_trash.Remove(name))
        {
            throw new SkillStorageException(
                $"No trashed skill named '{name}' exists. Delete operates on the trash only — move the skill there first.");
        }

        _siblings.Remove(name);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string name)
    {
        SkillGuards.ValidateNameOrThrow(name, nameof(name));
        return Task.FromResult(_active.ContainsKey(name));
    }

    public Task<string?> ReadRawAsync(string name)
    {
        SkillGuards.ValidateNameOrThrow(name, nameof(name));
        // The in-memory double has no raw bytes; it synthesizes the canonical
        // document so the adoption path can round-trip a known-good skill.
        return Task.FromResult(
            _active.TryGetValue(name, out var skill) ? SkillFileFormat.WriteDocument(skill) : null);
    }

    public Task RewriteAsync(Skill skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        SkillGuards.ValidateNameOrThrow(skill.Name, nameof(skill));

        if (skill.Origin == SkillOrigin.External)
        {
            throw new SkillStorageException(
                $"Skill '{skill.Name}' is externally originated; Eling never writes external skills.");
        }

        if (!_active.ContainsKey(skill.Name))
        {
            throw new SkillStorageException($"No skill named '{skill.Name}' exists to rewrite.");
        }

        _active[skill.Name] = skill;
        return Task.CompletedTask;
    }

    public string FilePath(string name)
    {
        SkillGuards.ValidateNameOrThrow(name, nameof(name));
        return $"memory://skills/{name}/SKILL.md";
    }
}
