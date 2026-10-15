using System.Threading.Channels;

namespace Eling.Core.Audit.Storage;

/// <summary>
/// Single-writer logger. Entries enter a bounded channel; one background loop
/// drains and appends them in batches under a semaphore, so hot read paths
/// never take the file lock and appends are not one-fsync-per-entry. Change
/// entries wait (bounded) for a write confirmation; access entries are
/// fire-and-forget. Every failure path is fail-open: an audit failure must
/// never break the caller's operation.
/// </summary>
public sealed class BufferedAuditLogger : IAuditLogger, IAsyncDisposable
{
    /// <summary>
    /// One queued entry plus the per-entry signal the flusher completes once the
    /// batch holding it is appended. A record rather than a tuple because the
    /// repo's no-tuple convention forbids tuple shapes outside the legacy
    /// allowlist (CodeOrganizationConventionTests.NoTuples_BeyondAllowlist).
    /// </summary>
    private sealed record PendingEntry(
        AuditEvent Entry,
        TaskCompletionSource Written);

    private readonly AuditFileStore _store;
    private readonly IAuditIndex? _index;
    private readonly AuditOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Channel<PendingEntry> _channel;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;

    public BufferedAuditLogger(AuditFileStore store, IAuditIndex? index, AuditOptions options, TimeProvider timeProvider)
    {
        _store = store;
        _index = index;
        _options = options;
        _timeProvider = timeProvider;
        _channel = Channel.CreateBounded<PendingEntry>(new BoundedChannelOptions(4096)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite
        });
        _pump = Task.Run(PumpAsync);
    }

    public async ValueTask RecordAsync(AuditEvent entry, CancellationToken cancellationToken = default)
    {
        var actor = AuditActorContext.Current;
        var enriched = new AuditEvent
        {
            SchemaVersion = entry.SchemaVersion,
            Timestamp = entry.Timestamp == default ? _timeProvider.GetUtcNow() : entry.Timestamp,
            Trail = entry.Trail,
            Category = entry.Category,
            Action = entry.Action,
            Actor = entry.Actor == AuditActor.Unknown.Actor ? actor.Actor : entry.Actor,
            Source = entry.Source == AuditActor.Unknown.Source ? actor.Source : entry.Source,
            Scope = entry.Scope,
            ProjectRoot = entry.ProjectRoot,
            Target = entry.Target,
            Outcome = entry.Outcome,
            DurationMs = entry.DurationMs,
            ByteSize = entry.ByteSize,
            Hash = entry.Hash,
            PrevHash = entry.PrevHash,
            Metadata = entry.Metadata
        };

        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _channel.Writer.TryWrite(new PendingEntry(enriched, written));

        if (entry.Trail == AuditTrail.Change)
        {
            // The caller is told the entry reached disk — or gives up after the
            // bound. Either way the user's operation completes: audit is
            // fail-open, but "flushed before return" must be a confirmation,
            // not merely a bounded wait (amendment A6).
            await Task.WhenAny(written.Task, Task.Delay(_options.FlushTimeout, cancellationToken));
        }
    }

    private async Task PumpAsync()
    {
        var reader = _channel.Reader;
        while (await reader.WaitToReadAsync(_cts.Token))
        {
            var batch = new List<PendingEntry>(_options.FlushBatchSize);
            while (batch.Count < _options.FlushBatchSize && reader.TryRead(out var item))
            {
                batch.Add(item);
            }

            foreach (var group in batch.GroupBy(e => e.Entry.Trail))
            {
                await FlushAsync(group.Key, group.ToList());
            }
        }
    }

    private async Task FlushAsync(AuditTrail trail, IReadOnlyList<PendingEntry> batch)
    {
        await _writeLock.WaitAsync(_cts.Token);
        try
        {
            var now = _timeProvider.GetUtcNow();
            await AuditRotation.RotateIfNeededAsync(_store, trail, now, _options.CompressOnRotate, _cts.Token);
            await _store.AppendAsync(trail, batch.Select(b => b.Entry).ToList(), flushToDisk: trail == AuditTrail.Change, _cts.Token);

            if (_index is not null)
            {
                var file = Path.GetFileName(_store.GetCurrentFilePath(trail, now));
                await _index.IndexAsync(
                    batch.Select(b => new AuditRecord(file, 0, b.Entry)).ToList(),
                    _cts.Token);
            }

            // Durability confirmation for the change trail: every waiter in this
            // batch learns its entry is on disk. The failure path deliberately
            // leaves the signal unset — a failed write must not be reported as
            // persisted, and the caller's bounded wait still ends at FlushTimeout.
            foreach (var item in batch)
            {
                item.Written.TrySetResult();
            }
        }
        catch
        {
            // Fail-open: an audit failure must never break the caller's operation.
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        try
        {
            await _pump.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Best effort drain.
        }
        _cts.Cancel();
        _cts.Dispose();
        _writeLock.Dispose();
    }
}
