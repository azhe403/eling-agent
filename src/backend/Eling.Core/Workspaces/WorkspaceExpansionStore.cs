using System.Text.Json;
using Eling.Core.Database;
using Eling.Core.Serialization;
using Microsoft.Extensions.Logging;

namespace Eling.Core.Workspaces;

/// <summary>
/// The shared store for the workspace tree's open/closed shape, so the desktop
/// app and the dashboard show the same tree.
/// </summary>
/// <remarks>
/// The backend owns the file: both clients read and write it through the
/// coordinator's endpoints, so there is exactly one copy and no client can
/// overwrite another's state. The file is written atomically (temp file plus
/// move) so a crash mid-write cannot leave a torn document.
/// <para>
/// Both clients can toggle at the same moment, so every read and write runs
/// under one named <see cref="CacheFileLock"/> — the same cross-process lock the
/// read caches use. Without it two writers share one temp file and the losing
/// <see cref="File.Move(string, string, bool)"/> throws, losing that toggle.
/// Each write also gets its own temp name, so even an unguarded write can never
/// collide with another.
/// </para>
/// <para>
/// A file this store cannot read is treated as an empty expansion rather than
/// an error: the tree falls back to its defaults, which is a working list.
/// </para>
/// </remarks>
public sealed class WorkspaceExpansionStore
{
    private const string LockPurpose = "workspace-expansion";

    private readonly string _filePath;
    private readonly string _tempPath;
    private readonly CacheFileLock _lock;
    private readonly ILogger? _logger;

    public WorkspaceExpansionStore(string filePath, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
        var dir = Path.GetDirectoryName(_filePath);
        _lock = new CacheFileLock(CacheFileLock.NameFor(
            string.IsNullOrEmpty(dir) ? Directory.GetCurrentDirectory() : dir,
            LockPurpose));

        // A per-instance temp name: guarded writers still serialise on the lock,
        // but a stray writer can no longer make another's Move fail.
        _tempPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        _logger = logger;
    }

    /// <summary>The file this store reads and writes.</summary>
    public string FilePath => _filePath;

    /// <summary>
    /// Reads the stored shape. A missing, empty or unreadable file yields an
    /// empty expansion.
    /// </summary>
    public WorkspaceExpansion Load()
    {
        return _lock.Run(() =>
        {
            if (!File.Exists(_filePath)) return Empty();

            try
            {
                var json = File.ReadAllText(_filePath);
                if (string.IsNullOrWhiteSpace(json)) return Empty();
                return JsonSerializer.Deserialize<WorkspaceExpansion>(json, JsonDefaults.Shared) ?? Empty();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                _logger?.LogWarning(ex, "Ignoring unreadable workspace expansion at {Path}.", _filePath);
                return Empty();
            }
        });
    }

    /// <summary>
    /// Writes the shape. Ids are de-duplicated and kept in order, so the file
    /// stays stable between writes.
    /// </summary>
    public void Save(WorkspaceExpansion expansion)
    {
        ArgumentNullException.ThrowIfNull(expansion);

        var normalized = new WorkspaceExpansion(
            Normalize(expansion.Expanded),
            Normalize(expansion.Collapsed));

        _lock.Run(() =>
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            try
            {
                File.WriteAllText(_tempPath, JsonSerializer.Serialize(normalized, JsonDefaults.Shared));
                File.Move(_tempPath, _filePath, overwrite: true);
            }
            catch
            {
                TryDeleteTemp();
                throw;
            }
        });
    }

    /// <summary>Removes the unique temp file, so a failed write leaves no litter.</summary>
    private void TryDeleteTemp()
    {
        try
        {
            if (File.Exists(_tempPath)) File.Delete(_tempPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a stray temp file is harmless, a crashing toggle is not.
        }
    }

    private static WorkspaceExpansion Empty() => new([], []);

    private static IReadOnlyList<string> Normalize(IReadOnlyList<string> ids)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (!seen.Add(id)) continue;
            result.Add(id);
        }
        return result;
    }
}
