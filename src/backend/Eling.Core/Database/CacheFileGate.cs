using System.Text.Json;

using Eling.Core.Serialization;
using Microsoft.Extensions.Logging;

namespace Eling.Core.Database;

/// <summary>
/// The file mechanics both read caches need: one named lock, a temp file, and
/// a rename over the real one. It existed twice — once in the SQL cache, once
/// in the Redis cache — as the same hundred-odd lines, so a fix to the
/// locking had to be made twice or it was made once and forgotten.
///
/// <para>
/// The lock is a <see cref="CacheFileLock"/> shared by name, so two caches over
/// the same name hold the same lock — and, because that lock is recursive for
/// its owning thread, one operation may span several files under it.
/// </para>
/// </summary>
internal sealed class CacheFileGate
{
    private const string TempSuffix = ".tmp";

    private readonly CacheFileLock _lock;
    private readonly string _path;
    private readonly ILogger? _logger;

    /// <param name="path">The file this gate guards.</param>
    /// <param name="lockName">
    /// The name of the lock that guards it. Gates over files in the same
    /// directory under the same purpose must share one name, so that one
    /// operation can span them; use
    /// <see cref="CacheFileLock.NameFor"/> to derive it.
    /// </param>
    public CacheFileGate(string path, string lockName, ILogger? logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(lockName);

        _path = path;
        _logger = logger;
        _lock = new CacheFileLock(lockName);
    }

    /// <summary>The file this gate guards.</summary>
    public string Path => _path;

    /// <summary>
    /// The lock this gate shares with any other gate of the same name. Held
    /// across several files when one operation must span them.
    /// </summary>
    public CacheFileLock Lock => _lock;

    /// <summary>
    /// Read the file, or a fresh empty value when it is missing, blank, or
    /// unreadable. An unreadable cache costs a refetch, never an exception.
    /// </summary>
    public T Load<T>(Func<T> empty)
    {
        if (!File.Exists(_path)) return empty();

        try
        {
            var json = File.ReadAllText(_path);
            if (string.IsNullOrWhiteSpace(json)) return empty();

            return JsonSerializer.Deserialize<T>(json, JsonDefaults.Shared) ?? empty();
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "Ignoring unreadable cache at {Path}.", _path);
            return empty();
        }
    }

    /// <summary>Write the file atomically: temp first, then rename over.</summary>
    public void Save<T>(T file)
    {
        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tempPath = _path + TempSuffix;
        File.WriteAllText(tempPath, JsonSerializer.Serialize(file, JsonDefaults.Shared));
        File.Move(tempPath, _path, overwrite: true);
    }

    /// <summary>Read under the lock.</summary>
    public T WithGate<T>(Func<T> action) => _lock.Run(action);

    /// <summary>Write under the lock.</summary>
    public void WithGate(Action action) => _lock.Run(action);
}
