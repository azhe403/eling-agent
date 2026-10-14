using System.Text.Json;
using Microsoft.Extensions.Logging;

using Eling.Core.Serialization;

namespace Eling.Core.Projects;

/// <summary>
/// JSON store for <see cref="IWorkspacesRegistry"/>: one entry per workspace
/// folder Eling has ever registered, persisted to a single file that every
/// runtime process can read and write.
/// </summary>
/// <remarks>
/// Grain is the folder, not the process — the file outlives whatever process
/// wrote it, which is the whole reason this store exists.
/// <para>
/// Writes are serialized by a named cross-process mutex (each Eling runtime
/// registers its own workspace on start) and land atomically via a temp file
/// plus <see cref="File.Move(string, string, bool)"/>, so a crash mid-write or
/// two processes writing at once can never leave a torn file. Every mutation
/// re-reads the file under that mutex, which is what keeps concurrent
/// registrations from overwriting each other's rows.
/// </para>
/// <para>
/// All file work happens synchronously inside the gate. That is deliberate:
/// <c>Record</c> is a void method on the interface and callers invoke it on
/// threads that own the mutex, so an <c>await</c> inside the critical section
/// could hop the continuation to a pool thread that no longer owns the mutex —
/// leaking the gate on the failed release. The files are tiny; synchronous I/O
/// is the correct trade, and the async method shapes exist for the interface,
/// not for concurrency.
/// </para>
/// <para>
/// A file this store cannot understand (wrong shape, corrupt JSON, or the
/// legacy bare string-array format) is treated as an empty registry and
/// rewritten on the next record. Rows are cheap to regenerate — a
/// registration re-discovers the folder.
/// </para>
/// </remarks>
public sealed class JsonWorkspaceRegistry : IWorkspacesRegistry, IDisposable
{
    private const string MutexName = @"Global\eling-workspaces-registry";
    private const string TempSuffix = ".tmp";

    /// <summary>
    /// Bounded for parity with the SQLite store's busy_timeout: a foreign
    /// holder that overruns the budget throws instead of parking the caller's
    /// thread (and any lock it holds) forever.
    /// </summary>
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// One mutex per name per process. Two <see cref="Mutex"/> objects over the
    /// same named kernel object in one process corrupt thread-ownership
    /// accounting — ReleaseMutex starts throwing "unsynchronized block" and the
    /// gate leaks — so every registry instance shares this one object.
    /// </summary>
    private static readonly Mutex Gate = new(initiallyOwned: false, MutexName);

    private readonly string _filePath;
    private readonly ILogger<JsonWorkspaceRegistry>? _logger;

    public JsonWorkspaceRegistry(
        string filePath,
        ILogger<JsonWorkspaceRegistry>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
        _logger = logger;
    }

    public void Record(RegisteredWorkspace workspace)
    {
        try
        {
            RecordCore(workspace);
        }
        catch (Exception ex)
        {
            // Recording must never be the reason a runtime fails to start.
            // Losing a row costs the dashboard a listing until it registers
            // again; refusing to boot costs everything.
            _logger?.LogWarning(ex, "Failed to record workspace {Root}.", workspace?.WorkspaceRoot);
        }
    }

    private void RecordCore(RegisteredWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (string.IsNullOrWhiteSpace(workspace.WorkspaceRoot)) return;
        if (string.Equals(workspace.WorkspaceRoot, "UserScope", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(workspace.HeadScopeRoot, "UserScope", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!Path.IsPathFullyQualified(workspace.WorkspaceRoot))
        {
            return;
        }

        var root = Trimmed(workspace.WorkspaceRoot);
        WithGate(() =>
        {
            var rows = LoadCore();
            var refreshed = false;
            for (var i = 0; i < rows.Count; i++)
            {
                if (!string.Equals(rows[i].WorkspaceRoot, root, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // First-seen is history, not something a re-registration rewrites.
                // The id is deliberately absent too: a folder keeps the id it
                // was first written with. Last-seen and the rest come from the
                // caller's registration, exactly like the SQL upsert before it.
                rows[i] = rows[i] with
                {
                    HeadScopeRoot = workspace.HeadScopeRoot,
                    CodebaseEnabled = workspace.CodebaseEnabled,
                    LastSeenAt = workspace.LastSeenAt,
                };
                refreshed = true;
                break;
            }

            if (!refreshed)
            {
                rows.Add(new RegisteredWorkspace(
                    WorkspaceId: string.IsNullOrWhiteSpace(workspace.WorkspaceId)
                        ? System.Ulid.NewUlid().ToString().ToLowerInvariant()
                        : workspace.WorkspaceId,
                    WorkspaceRoot: root,
                    HeadScopeRoot: workspace.HeadScopeRoot,
                    CodebaseEnabled: workspace.CodebaseEnabled,
                    FirstSeenAt: workspace.FirstSeenAt,
                    LastSeenAt: workspace.LastSeenAt));
            }

            SaveCore(rows);
        });
    }

    public Task<IReadOnlyList<RegisteredWorkspace>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Task.FromResult(WithGate(() => Ordered(LoadCore())));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to list workspaces from {Path}.", _filePath);
            return Task.FromResult<IReadOnlyList<RegisteredWorkspace>>([]);
        }
    }

    public Task<RegisteredWorkspace?> FindByRootAsync(
        string root,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)) return Task.FromResult<RegisteredWorkspace?>(null);
        var normalized = Trimmed(root);
        try
        {
            return Task.FromResult(
                WithGate(() =>
                {
                    var rows = LoadCore();
                    RegisteredWorkspace? found = rows.FirstOrDefault(r =>
                        string.Equals(r.WorkspaceRoot, normalized, StringComparison.OrdinalIgnoreCase));
                    return found;
                }));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to find workspace {Root} in {Path}.", root, _filePath);
            return Task.FromResult<RegisteredWorkspace?>(null);
        }
    }

    public Task<bool> DeleteByRootAsync(
        string root,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)) return Task.FromResult(false);
        if (!File.Exists(_filePath)) return Task.FromResult(false);
        var normalized = Trimmed(root);
        try
        {
            return Task.FromResult(
                WithGate(() =>
                {
                    var rows = LoadCore();
                    var removed = rows.RemoveAll(r =>
                        string.Equals(r.WorkspaceRoot, normalized, StringComparison.OrdinalIgnoreCase));
                    if (removed > 0)
                    {
                        SaveCore(rows);
                    }

                    return removed > 0;
                }));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to delete workspace {Root} from {Path}.", root, _filePath);
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Deletes every row whose workspace folder is gone, keeping rows whose
    /// folder still exists even when their process is dead — a codebase index
    /// stays readable after its owner exits, which is the reason the catalogue
    /// exists. A row pointing at a folder that no longer exists is nothing but
    /// a listing entry for a folder nobody can open. The file is rewritten only
    /// when something was actually removed, so a clean catalogue is never
    /// rewritten just because a prune pass ran.
    /// </summary>
    public Task<int> DeleteMissingRootsAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Task.FromResult(
                WithGate(() =>
                {
                    var rows = LoadCore();
                    var kept = rows.Where(r => Directory.Exists(r.WorkspaceRoot)).ToList();
                    var removed = rows.Count - kept.Count;
                    if (removed > 0)
                    {
                        SaveCore(kept);
                    }

                    return removed;
                }));
        }
        catch (Exception ex)
        {
            // Same contract as the other mutations: a failed prune costs the
            // catalogue a few dead rows, and must never take the caller down.
            _logger?.LogWarning(ex, "Failed to prune workspaces with missing roots from {Path}.", _filePath);
            return Task.FromResult(0);
        }
    }

    private void WithGate(Action action)
    {
        EnterGate();
        try
        {
            action();
        }
        finally
        {
            Gate.ReleaseMutex();
        }
    }

    private T WithGate<T>(Func<T> action)
    {
        EnterGate();
        try
        {
            return action();
        }
        finally
        {
            Gate.ReleaseMutex();
        }
    }

    /// <summary>
    /// Takes the named mutex, bounded. A holder that died without releasing
    /// throws <see cref="AbandonedMutexException"/> — the exception still hands
    /// us ownership, which is the correct outcome: the file, not the dead lock
    /// owner, is the truth. A live holder that overruns the budget throws
    /// instead of parking the caller's thread forever.
    /// </summary>
    private void EnterGate()
    {
        try
        {
            if (!Gate.WaitOne(GateTimeout))
            {
                throw new TimeoutException(
                    $"Timed out after {GateTimeout} waiting for the workspace registry gate.");
            }
        }
        catch (AbandonedMutexException)
        {
        }
    }

    private static IReadOnlyList<RegisteredWorkspace> Ordered(List<RegisteredWorkspace> rows) =>
        rows
            .OrderByDescending(r => r.LastSeenAt)
            .ThenByDescending(r => r.WorkspaceId, StringComparer.Ordinal)
            .ToList();

    private List<RegisteredWorkspace> LoadCore()
    {
        if (!File.Exists(_filePath)) return [];
        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<List<RegisteredWorkspace>>(json, JsonDefaults.Shared) ?? [];
        }
        catch (JsonException ex)
        {
            // Wrong shape (e.g. the legacy string-array format) or corruption:
            // an unreadable registry is an empty registry, rewritten next record.
            _logger?.LogWarning(ex, "Ignoring unreadable workspace registry at {Path}.", _filePath);
            return [];
        }
    }

    private void SaveCore(List<RegisteredWorkspace> rows)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tempPath = _filePath + TempSuffix;
        var json = JsonSerializer.Serialize(rows, JsonDefaults.Shared);
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _filePath, overwrite: true);
    }

    private static string Trimmed(string root)
        => Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public void Dispose()
    {
        // The mutex is process-shared: disposing it here would yank the gate
        // from under every other registry instance. It dies with the process,
        // one handle, like SqliteWorkspacesRegistry before it.
    }
}
