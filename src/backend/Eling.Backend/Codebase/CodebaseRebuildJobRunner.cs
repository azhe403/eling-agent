using Eling.Backend.Dtos;
using Eling.Core.Codebase;
using Eling.Core.Memory;
using Eling.Core.Scope;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Codebase;

/// <summary>
/// Runs at most one codebase rebuild job at a time across a resolved set of
/// project roots, publishing an immutable snapshot after every root so the
/// dashboard can follow progress over SSE.
/// <para>
/// The local project reuses the injected <see cref="CodebaseIndexService"/>
/// so the job shares its write gate with <c>CodebaseWatcherService</c>;
/// siblings get their own service over their own DB file. Index writes are
/// per-file transactional, so losing this in-memory job (host restart) leaves
/// every DB consistent — it only loses the progress display.
/// </para>
/// </summary>
public sealed class CodebaseRebuildJobRunner
{
    private readonly object _lock = new();
    private readonly CodebaseIndexService _local;
    private readonly CodebaseRebuildBroadcaster _broadcaster;
    private readonly IMemoryChangeNotifier _notifier;
    private readonly ILogger<CodebaseRebuildJobRunner> _logger;
    private readonly Func<string, bool, CancellationToken, Task<CodebaseIndexResult>> _reindex;
    private readonly CancellationToken _stopping;
    private readonly List<CodebaseRebuildProjectResult> _results = [];

    private CodebaseRebuildRequest? _request;
    private string? _jobId;
    private string? _currentProject;
    private string? _error;
    private bool _isRunning;

    /// <param name="local">This backend's own index service, reused for the local root.</param>
    /// <param name="broadcaster">Publishes progress snapshots to the rebuild SSE channel.</param>
    /// <param name="notifier">Notified after every root so index tiles refresh, as before.</param>
    /// <param name="logger">Diagnostics for job and per-project outcomes.</param>
    /// <param name="lifetime">
    /// Supplies the shutdown token so a rebuild in flight stops with the host.
    /// </param>
    /// <param name="reindex">
    /// Indexes one root in one mode. Defaults to the real per-root pass;
    /// overridden in tests to observe calls without touching disk.
    /// </param>
    public CodebaseRebuildJobRunner(
        CodebaseIndexService local,
        CodebaseRebuildBroadcaster broadcaster,
        IMemoryChangeNotifier notifier,
        ILogger<CodebaseRebuildJobRunner> logger,
        IHostApplicationLifetime? lifetime = null,
        Func<string, bool, CancellationToken, Task<CodebaseIndexResult>>? reindex = null)
    {
        _local = local;
        _broadcaster = broadcaster;
        _notifier = notifier;
        _logger = logger;
        _reindex = reindex ?? ReindexAsync;
        _stopping = lifetime?.ApplicationStopping ?? CancellationToken.None;
    }

    /// <summary>
    /// Starts a rebuild, or returns the running job unchanged when one is
    /// already in flight. Concurrent clicks therefore attach to the same job
    /// instead of queueing a second pass over the same DB files.
    /// </summary>
    public CodebaseRebuildProgress Start(CodebaseRebuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        CodebaseRebuildProgress snapshot;
        lock (_lock)
        {
            if (_isRunning && _request is not null)
                return BuildSnapshot();

            _jobId = Guid.NewGuid().ToString("N");
            _request = request;
            _currentProject = null;
            _error = null;
            _isRunning = true;
            _results.Clear();
            snapshot = BuildSnapshot();
        }

        _logger.LogInformation(
            "Codebase rebuild job {JobId} started: scope={Scope} full={Full} targets={Targets} skipped={Skipped}",
            snapshot.JobId, request.Scope, request.Full, snapshot.Total, request.Skipped.Count);

        _broadcaster.Publish(snapshot);
        _ = Task.Run(() => RunAsync(snapshot.JobId, _stopping), CancellationToken.None);
        return snapshot;
    }

    /// <summary>The snapshot for <paramref name="jobId"/>, or null when unknown.</summary>
    public CodebaseRebuildProgress? Get(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId)) return null;
        lock (_lock)
        {
            return string.Equals(_jobId, jobId, StringComparison.Ordinal) ? BuildSnapshot() : null;
        }
    }

    private async Task RunAsync(string jobId, CancellationToken ct)
    {
        var targets = TargetsFor(jobId);
        var full = FullFor(jobId);
        try
        {
            foreach (var root in targets)
            {
                ct.ThrowIfCancellationRequested();
                lock (_lock) { _currentProject = root; }
                Publish();

                var result = await IndexOneAsync(root, full, ct).ConfigureAwait(false);
                lock (_lock) { _results.Add(result); }

                _logger.LogInformation(
                    "Codebase rebuild job {JobId} finished {Root} ok={Ok} files={Files} chunks={Chunks} error={Error}",
                    jobId, root, result.Ok, result.Files, result.Chunks, result.Error);

                // Existing behaviour: every index write refreshes the tiles.
                await _notifier.NotifyAsync("codebase", ct).ConfigureAwait(false);
                Publish();
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Codebase rebuild job {JobId} cancelled by host shutdown.", jobId);
        }
        catch (Exception ex)
        {
            // Per-root passes are guarded, so reaching here means something
            // structural failed (e.g. the notifier or the broadcaster).
            _logger.LogError(ex, "Codebase rebuild job {JobId} aborted.", jobId);
            lock (_lock) { _error = ex.Message; }
        }
        finally
        {
            lock (_lock)
            {
                _isRunning = false;
                _currentProject = null;
            }
            Publish();
        }
    }

    private async Task<CodebaseRebuildProjectResult> IndexOneAsync(
        string root,
        bool full,
        CancellationToken ct)
    {
        try
        {
            var result = await _reindex(root, full, ct).ConfigureAwait(false);

            // Single-flight turned this pass away because another one already
            // held the root's gate. Nothing was indexed, so report it as not
            // done rather than as a successful zero-file pass.
            if (result.Status is not null)
            {
                return new CodebaseRebuildProjectResult(
                    root, false, 0, 0, 0, 0, result.Status);
            }

            return new CodebaseRebuildProjectResult(
                root, true, result.Files, result.Chunks, result.Skipped, result.Deleted, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One locked or corrupt DB must not fail the whole job.
            return new CodebaseRebuildProjectResult(root, false, 0, 0, 0, 0, ex.Message);
        }
    }

    /// <summary>
    /// The real per-root pass: the local service for this backend's own
    /// workspace, a fresh service over the sibling's DB file otherwise.
    /// </summary>
    private async Task<CodebaseIndexResult> ReindexAsync(string root, bool full, CancellationToken ct)
    {
        if (string.Equals(Path.GetFullPath(root), _local.ProjectRoot, StringComparison.OrdinalIgnoreCase))
            return await _local.IndexAsync(full: full, ct: ct).ConfigureAwait(false);

        // Read-write on purpose, unlike the read paths in CodebaseIndexService:
        // a rebuild is the one operation that legitimately writes a sibling's
        // DB. Roots still arrive pre-validated (ElingPaths
        // .EnsureCodebaseRootsAllowed via CodebaseEndpoints.ResolveScope), so
        // this cannot be steered at an excluded or arbitrary directory.
        var dbPath = ElingPaths.ResolveCodebaseDbPath(root);
        using var index = new SqliteCodebaseIndex(dbPath);
        var sibling = new CodebaseIndexService(root, index);
        return await sibling.IndexAsync(full: full, ct: ct).ConfigureAwait(false);
    }

    private IReadOnlyList<string> TargetsFor(string jobId)
    {
        lock (_lock)
        {
            if (!string.Equals(_jobId, jobId, StringComparison.Ordinal) || _request is null) return [];
            return _request.Targets;
        }
    }

    private bool FullFor(string jobId)
    {
        lock (_lock)
        {
            if (!string.Equals(_jobId, jobId, StringComparison.Ordinal) || _request is null) return false;
            return _request.Full;
        }
    }

    private void Publish()
    {
        CodebaseRebuildProgress snapshot;
        lock (_lock)
        {
            snapshot = BuildSnapshot();
        }
        _broadcaster.Publish(snapshot);
    }

    private CodebaseRebuildProgress BuildSnapshot()
    {
        var request = _request ?? new CodebaseRebuildRequest("project", [], [], false);
        return new CodebaseRebuildProgress(
            _jobId ?? string.Empty,
            request.Scope,
            request.Full,
            _isRunning,
            _results.Count,
            request.Targets.Count,
            request.Skipped,
            _currentProject,
            _results.ToList(),
            _error);
    }
}
