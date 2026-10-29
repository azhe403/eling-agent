using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Eling.Core.Database;

/// <summary>
/// A named mutex, shared by every caller that names it.
///
/// <para>
/// One <see cref="Mutex"/> object per name per process. Two <see cref="Mutex"/>
/// objects over the same named kernel object in one process corrupt
/// thread-ownership accounting — ReleaseMutex starts throwing "object
/// synchronization method was called from an unsynchronized block" and the
/// gate never lets go. The kernel object is shared by name across processes,
/// so the wrapper object must be shared by name inside this one.
/// </para>
/// <para>
/// The mutex is recursive for its owning thread, so a holder may take it again
/// without deadlocking. That is what lets one logical operation span several
/// files: the caller takes the lock once and does the work under it.
/// </para>
/// </summary>
internal sealed class CacheFileLock
{
    private static readonly ConcurrentDictionary<string, Mutex> Locks = new(StringComparer.Ordinal);

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly Mutex _mutex;
    private readonly string _name;

    public CacheFileLock(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _name = name;
        _mutex = Locks.GetOrAdd(name, n => new Mutex(initiallyOwned: false, n));
    }

    /// <summary>The name this lock is shared under.</summary>
    public string Name => _name;

    /// <summary>
    /// The lock name for the files in one directory under one purpose.
    ///
    /// <para>
    /// Deriving it from the directory rather than taking it as a free string
    /// means a caller cannot pair one file with another file's lock — the
    /// mismatch that lets two writers into one file. The directory is hashed
    /// because a Windows object name has a length limit and a path can be
    /// longer than it; the hash also keeps the name legal for any path.
    /// </para>
    /// <para>
    /// The purpose keeps two caches in one directory apart: the SQL cache and
    /// the Redis cache share a data directory but have nothing to do with each
    /// other's files.
    /// </para>
    /// </summary>
    public static string NameFor(string directory, string purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(directory)));
        return $@"Global\eling-cache-{purpose}-{Convert.ToHexString(bytes)[..16].ToLowerInvariant()}";
    }

    /// <summary>Run an action under the lock.</summary>
    public void Run(Action action) => Run<object?>(() =>
    {
        action();
        return null;
    });

    /// <summary>Run a function under the lock and return its answer.</summary>
    public T Run<T>(Func<T> action)
    {
        var held = false;
        try
        {
            try
            {
                held = _mutex.WaitOne(Timeout);
            }
            catch (AbandonedMutexException)
            {
                // The previous holder died inside the lock, but we are the owner
                // now. The file is written atomically, so what is on disk is
                // whole; carry on.
                held = true;
            }

            if (!held)
            {
                throw new TimeoutException(
                    $"Timed out after {Timeout} waiting for the cache lock '{_name}'.");
            }

            return action();
        }
        finally
        {
            // Only release what we hold: releasing a lock we never took would
            // throw and take the process's mutex accounting with it.
            if (held) _mutex.ReleaseMutex();
        }
    }
}
