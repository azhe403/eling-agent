using Eling.Core.Scope;
using Eling.Core.Skill.Serialization;
using Microsoft.Extensions.Logging;

namespace Eling.Core.Skill.Storage;

/// <summary>
/// File-backed skill storage rooted at a catalog root (default: <c>~/.agents</c>,
/// via <see cref="SkillCatalogPaths.CatalogDirectory"/>). Skills live in
/// <c>{catalogRoot}/skills/{name}/SKILL.md</c>; the trash is the sibling
/// <c>{catalogRoot}/.trash/skills/{name}/</c> — tests pass a temp
/// <paramref name="catalogRoot"/> so the real catalog is never touched.
/// Writes are atomic: a temp sibling folder is created, filled, then moved into
/// place (same volume, so <see cref="Directory.Move"/> is a rename). On any
/// failure no partial skill remains; overwriting an existing active skill swaps
/// it through a backup folder with rollback on error. Trash destinations are
/// never merged — a colliding destination is a <see cref="SkillConflictException"/>.
/// IO failures surface as <see cref="SkillStorageException"/>; they are never
/// swallowed. <see cref="Origin"/> is read as <c>eling</c> on every successful
/// parse (only Eling writes frontmatter); saving a skill whose
/// <see cref="Skill.Origin"/> is <c>external</c> is refused.
/// </summary>
public class FileSystemSkillStorage : ISkillStorage
{
    public const string SkillFileName = "SKILL.md";
    private const string TemporaryFolderPrefix = ".tmp-";
    private const string BackupFolderPrefix = ".bak-";

    private readonly string _catalogRoot;
    private readonly string _skillsDir;
    private readonly string _trashRoot;
    private readonly ILogger<FileSystemSkillStorage>? _logger;

    public FileSystemSkillStorage(string? catalogRoot = null, ILogger<FileSystemSkillStorage>? logger = null)
    {
        _catalogRoot = Path.GetFullPath(catalogRoot ?? SkillCatalogPaths.CatalogDirectory);
        _skillsDir = Path.Combine(_catalogRoot, SkillCatalogPaths.SkillsSubdir);
        _trashRoot = Path.Combine(_catalogRoot, SkillCatalogPaths.TrashDirName, SkillCatalogPaths.SkillsSubdir);
        _logger = logger;
    }

    /// <summary>The catalog root this instance writes to (absolute path).</summary>
    public string CatalogRoot => _catalogRoot;

    public async Task SaveAsync(Skill skill, IReadOnlyList<SkillSiblingFile>? siblingFiles = null)
    {
        ArgumentNullException.ThrowIfNull(skill);
        SkillGuards.ValidateNameOrThrow(skill.Name, nameof(skill));
        ValidateSiblingFilesOrThrow(siblingFiles);
        // Vocabulary/invariant failures must happen before any IO so no temp folder is left behind.
        SkillFileFormat.ValidateForWrite(skill);

        if (skill.Origin == SkillOrigin.External)
        {
            throw new SkillStorageException(
                $"Skill '{skill.Name}' is externally originated; Eling never writes external skills.");
        }

        if (Directory.Exists(TrashFolder(skill.Name)))
        {
            throw new SkillConflictException(
                $"A trashed skill named '{skill.Name}' occupies the name. Restore it or delete it permanently before saving a new skill with this name.");
        }

        var destination = ActiveFolder(skill.Name);
        var temp = Path.Combine(_skillsDir, $"{TemporaryFolderPrefix}{skill.Name}-{Guid.NewGuid():N}");
        string? backup = null;

        try
        {
            Directory.CreateDirectory(_skillsDir);
            Directory.CreateDirectory(temp);

            var document = SkillFileFormat.WriteDocument(skill);
            await File.WriteAllTextAsync(Path.Combine(temp, SkillFileName), document);

            if (siblingFiles is { Count: > 0 })
            {
                foreach (var sibling in siblingFiles)
                {
                    var target = ResolveSiblingPath(temp, sibling.RelativePath);
                    var parent = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(parent))
                    {
                        Directory.CreateDirectory(parent);
                    }

                    await File.WriteAllBytesAsync(target, sibling.Content);
                }
            }

            if (Directory.Exists(destination))
            {
                backup = Path.Combine(_skillsDir, $"{BackupFolderPrefix}{skill.Name}-{Guid.NewGuid():N}");
                Directory.Move(destination, backup);
            }

            Directory.Move(temp, destination);
            temp = string.Empty; // consumed
            if (backup is not null)
            {
                TryDeleteDirectory(backup, "backup folder after replace");
                backup = null;
            }
        }
        catch (Exception ex)
        {
            RollbackSave(temp, destination, backup, ex);
            throw ex is SkillStorageException or ArgumentException
                ? ex
                : new SkillStorageException($"Failed to save skill '{skill.Name}': {ex.Message}", ex);
        }
    }

    public async Task<Skill?> GetByNameAsync(string name)
    {
        SkillGuards.ValidateNameOrThrow(name, nameof(name));

        var file = Path.Combine(ActiveFolder(name), SkillFileName);
        if (!File.Exists(file))
        {
            return null;
        }

        return await LoadFromFileAsync(file, name);
    }

    public async Task<IReadOnlyList<Skill>> ListAllAsync()
    {
        if (!Directory.Exists(_skillsDir))
        {
            return Array.Empty<Skill>();
        }

        var skills = new List<Skill>();
        foreach (var directory in Directory.EnumerateDirectories(_skillsDir))
        {
            var folderName = Path.GetFileName(directory);

            if (folderName.StartsWith('.'))
            {
                // Our own temp/backup artifacts from an interrupted write.
                _logger?.LogDebug("Skipping transient skill folder {Folder}.", directory);
                continue;
            }

            if (!Skill.ValidateName(folderName))
            {
                _logger?.LogWarning("Skipping folder {Folder}: not a valid kebab-case skill name.", directory);
                continue;
            }

            var file = Path.Combine(directory, SkillFileName);
            if (!File.Exists(file))
            {
                _logger?.LogWarning("Skipping skill folder {Folder}: SKILL.md is missing.", directory);
                continue;
            }

            try
            {
                skills.Add(await LoadFromFileAsync(file, folderName));
            }
            // SkillFrontMatterException derives from SkillStorageException.
            catch (SkillStorageException ex)
            {
                _logger?.LogWarning(ex, "Skipping skill folder {Folder}: {Message}", directory, ex.Message);
            }
        }

        return skills.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
    }

    public async Task RenameAsync(string oldName, string newName)
    {
        SkillGuards.ValidateNameOrThrow(oldName, nameof(oldName));
        SkillGuards.ValidateNameOrThrow(newName, nameof(newName));

        if (string.Equals(oldName, newName, StringComparison.Ordinal))
        {
            return;
        }

        var source = ActiveFolder(oldName);
        var destination = ActiveFolder(newName);

        if (!Directory.Exists(source))
        {
            throw new SkillStorageException($"No skill named '{oldName}' exists to rename.");
        }

        if (Directory.Exists(destination))
        {
            throw new SkillConflictException($"A skill named '{newName}' already exists; refusing to overwrite.");
        }

        if (Directory.Exists(TrashFolder(newName)))
        {
            throw new SkillConflictException(
                $"A trashed skill named '{newName}' occupies the name. Restore it or delete it permanently before reusing this name.");
        }

        var existing = await GetByNameAsync(oldName)
            ?? throw new SkillStorageException($"Skill folder '{source}' has no SKILL.md to rename.");

        var renamed = new Skill(
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

        SkillFileFormat.ValidateForWrite(renamed);

        var temp = Path.Combine(_skillsDir, $"{TemporaryFolderPrefix}{newName}-{Guid.NewGuid():N}");
        var movedToTemp = false;
        var rewritten = false;

        try
        {
            Directory.Move(source, temp);
            movedToTemp = true;

            var file = Path.Combine(temp, SkillFileName);
            var tempFile = Path.Combine(temp, $".SKILL-{Guid.NewGuid():N}.tmp");
            await File.WriteAllTextAsync(tempFile, SkillFileFormat.WriteDocument(renamed));
            File.Move(tempFile, file, overwrite: true);
            rewritten = true;

            Directory.Move(temp, destination);
        }
        catch (Exception ex)
        {
            RollbackRename(source, temp, movedToTemp, rewritten, existing, ex);
            throw ex is SkillStorageException or SkillFrontMatterException
                ? ex
                : new SkillStorageException($"Failed to rename skill '{oldName}' to '{newName}': {ex.Message}", ex);
        }
    }

    public Task MoveToTrashAsync(string name)
    {
        SkillGuards.ValidateNameOrThrow(name, nameof(name));

        var source = ActiveFolder(name);
        var destination = TrashFolder(name);

        if (!Directory.Exists(source))
        {
            throw new SkillStorageException($"No skill named '{name}' exists to move to trash.");
        }

        if (Directory.Exists(destination))
        {
            throw new SkillConflictException(
                $"The trash already contains a skill named '{name}' (a different, earlier-trashed skill). Restore or delete it permanently first; refusing to merge.");
        }

        try
        {
            Directory.CreateDirectory(_trashRoot);
            Directory.Move(source, destination);
        }
        catch (Exception ex)
        {
            throw new SkillStorageException($"Failed to move skill '{name}' to trash: {ex.Message}", ex);
        }

        return Task.CompletedTask;
    }

    public Task RestoreAsync(string name)
    {
        SkillGuards.ValidateNameOrThrow(name, nameof(name));

        var source = TrashFolder(name);
        var destination = ActiveFolder(name);

        if (!Directory.Exists(source))
        {
            throw new SkillStorageException($"No trashed skill named '{name}' exists to restore.");
        }

        if (Directory.Exists(destination))
        {
            throw new SkillConflictException($"An active skill named '{name}' already exists; refusing to overwrite.");
        }

        try
        {
            Directory.CreateDirectory(_skillsDir);
            Directory.Move(source, destination);
        }
        catch (Exception ex)
        {
            throw new SkillStorageException($"Failed to restore skill '{name}': {ex.Message}", ex);
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string name)
    {
        SkillGuards.ValidateNameOrThrow(name, nameof(name));

        var trashed = TrashFolder(name);
        if (!Directory.Exists(trashed))
        {
            throw new SkillStorageException(
                $"No trashed skill named '{name}' exists. Delete operates on the trash only — move the skill there first.");
        }

        try
        {
            Directory.Delete(trashed, recursive: true);
        }
        catch (Exception ex)
        {
            throw new SkillStorageException($"Failed to permanently delete trashed skill '{name}': {ex.Message}", ex);
        }

        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string name)
    {
        SkillGuards.ValidateNameOrThrow(name, nameof(name));
        return Task.FromResult(File.Exists(Path.Combine(ActiveFolder(name), SkillFileName)));
    }

    private async Task<Skill> LoadFromFileAsync(string file, string expectedName)
    {
        string raw;
        try
        {
            raw = await File.ReadAllTextAsync(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SkillStorageException($"Failed to read '{file}': {ex.Message}", ex);
        }

        return SkillFileFormat.ParseSkill(raw, expectedName);
    }

    private string ActiveFolder(string name)
    {
        var folder = Path.Combine(_skillsDir, name);
        EnsureUnderRoot(folder, _skillsDir);
        return folder;
    }

    private string TrashFolder(string name)
    {
        var folder = Path.Combine(_trashRoot, name);
        EnsureUnderRoot(folder, _trashRoot);
        return folder;
    }

    private static void EnsureUnderRoot(string folder, string root)
    {
        var fullFolder = Path.GetFullPath(folder);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullFolder.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new SkillStorageException($"Path '{folder}' escapes the catalog root; refusing.");
        }
    }

    /// <summary>
    /// Validates sibling-file paths before any IO. Rejected: absolute paths,
    /// rooted <c>\</c>-paths (leading separator on either OS), drive letters
    /// (<c>:</c> anywhere in a segment), <c>.</c>/<c>..</c>/empty segments.
    /// The resolved full path must stay under <paramref name="baseFolder"/>.
    /// </summary>
    private static string ResolveSiblingPath(string baseFolder, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("Sibling file path must not be empty.", nameof(relativePath));
        }

        if (Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException($"Sibling file path '{relativePath}' is absolute; refusing.", nameof(relativePath));
        }

        var segments = relativePath.Split(['/', '\\']);
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.Contains(':'))
            {
                throw new ArgumentException(
                    $"Sibling file path '{relativePath}' contains an illegal segment ('{segment}'); refusing.",
                    nameof(relativePath));
            }
        }

        var combined = Path.GetFullPath(Path.Combine(baseFolder, Path.Combine(segments)));
        var fullBase = Path.GetFullPath(baseFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(fullBase, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Sibling file path '{relativePath}' escapes the skill folder; refusing.", nameof(relativePath));
        }

        return combined;
    }

    private static void ValidateSiblingFilesOrThrow(IReadOnlyList<SkillSiblingFile>? siblingFiles)
    {
        if (siblingFiles is null)
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sibling in siblingFiles)
        {
            ArgumentNullException.ThrowIfNull(sibling);
            // Validate now, before any directory is created.
            var normalized = ResolveSiblingPath(Path.GetTempPath(), sibling.RelativePath);
            if (!seen.Add(normalized))
            {
                throw new ArgumentException($"Duplicate sibling file path '{sibling.RelativePath}'.", nameof(siblingFiles));
            }
        }
    }

    private void RollbackSave(string temp, string destination, string? backup, Exception cause)
    {
        try
        {
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Rollback: could not remove temp folder {Temp}.", temp);
        }

        if (backup is null)
        {
            return;
        }

        try
        {
            if (Directory.Exists(destination))
            {
                // Never delete a folder we cannot prove is ours. Leave both the
                // destination and the backup untouched and report loudly.
                _logger?.LogCritical(
                    "Rollback: destination {Destination} unexpectedly exists after a failed save; leaving it untouched and preserving the replaced skill at backup folder {Backup}. Cause: {Cause}",
                    destination,
                    backup,
                    cause.Message);
                return;
            }

            if (Directory.Exists(backup))
            {
                Directory.Move(backup, destination);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogCritical(
                ex,
                "Rollback failed; the replaced skill is preserved at backup folder {Backup}. Cause: {Cause}",
                backup,
                cause.Message);
        }
    }

    private void RollbackRename(string source, string temp, bool movedToTemp, bool rewritten, Skill original, Exception cause)
    {
        if (!movedToTemp || !Directory.Exists(temp))
        {
            return;
        }

        try
        {
            if (rewritten)
            {
                // Restore the old frontmatter so the moved-back folder is coherent.
                var file = Path.Combine(temp, SkillFileName);
                var tempFile = Path.Combine(temp, $".SKILL-{Guid.NewGuid():N}.tmp");
                File.WriteAllText(tempFile, SkillFileFormat.WriteDocument(original));
                File.Move(tempFile, file, overwrite: true);
            }

            Directory.Move(temp, source);
        }
        catch (Exception ex)
        {
            _logger?.LogCritical(
                ex,
                "Skill rename failed and rollback failed; the partially renamed skill is preserved at {Temp}. Cause: {Cause}",
                temp,
                cause.Message);
        }
    }

    private void TryDeleteDirectory(string path, string what)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not remove {What} at {Path}.", what, path);
        }
    }

}
