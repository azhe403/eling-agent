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
    /// <summary>Slots the bounded channel offers before callers start waiting.</summary>
    private const int Capacity = 4096;

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
        // A batch size of zero (or less) makes the pump's drain loop fill no
        // batch at all, so it spins through the channel without ever appending:
        // every change entry would wait out its FlushTimeout with nothing
        // written. Rejected here, at construction, rather than discovered at
        // the first flush.
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.FlushBatchSize);

        _store = store;
        _index = index;
        _options = options;
        _timeProvider = timeProvider;
        _channel = Channel.CreateBounded<PendingEntry>(new BoundedChannelOptions(Capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            // Wait, never drop: the change trail must not silently lose an entry
            // (design §2). A dropped entry is never written, its confirmation
            // never fires, and the caller returns as if the write happened —
            // fabricated durability. Waiting only delays the caller, bounded by
            // FlushTimeout below, which §14 fail-open allows.
            FullMode = BoundedChannelFullMode.Wait
        });
        _pump = Task.Run(PumpAsync);
    }

    public async ValueTask RecordAsync(AuditEvent entry, CancellationToken cancellationToken = default)
    {
        var actor = AuditActorContext.Current;
        var enriched = new AuditEvent
        {
            SchemaVersion = entry.SchemaVersion,
            // The logger owns the timestamp (design §4.1, amendment C2): an
            // absent one is stamped now, a caller's is normalized to UTC. A
            // caller's local offset is advisory data, not a storage-format
            // decision, so no artifact carries a +02:00 the logger accepted.
            Timestamp = entry.Timestamp == default
                ? _timeProvider.GetUtcNow()
                : new DateTimeOffset(entry.Timestamp.UtcDateTime, TimeSpan.Zero),
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
            Query = entry.Query,
            ResultCount = entry.ResultCount,
            Path = entry.Path,
            Matches = entry.Matches,
            Pid = entry.Pid,
            Metadata = entry.Metadata,
            PreviousMetadata = entry.PreviousMetadata
        };

        // No cancellation token on the enqueue on purpose: cancelling a queued
        // write removes the entry from the channel, which is the silent drop
        // this logger must not do. The caller's token bounds the wait below.
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var write = _channel.Writer.WriteAsync(new PendingEntry(enriched, written)).AsTask();

        if (entry.Trail != AuditTrail.Change)
        {
            // Access trail is fire-and-forget (§7.3): the caller never waits. The
            // entry is still queued rather than dropped, so a full channel delays
            // the pump, not the caller.
            _ = SettleAsync(write);
            return;
        }

        // The caller is told the entry reached disk — or gives up after the
        // bound. Either way the user's operation completes: audit is fail-open,
        // but "flushed before return" must be a confirmation, not merely a
        // bounded wait (amendment A6). One FlushTimeout budget covers the
        // enqueue and the confirmation alike, so a backed-up channel cannot
        // double the wait.
        var budget = Task.Delay(_options.FlushTimeout, cancellationToken);
        if (await Task.WhenAny(write, budget) == write)
        {
            await SettleAsync(write);
            await Task.WhenAny(written.Task, budget);
            return;
        }

        // The enqueue itself outran the bound: the entry is still queued and the
        // pump still owns it, so nothing about durability may be claimed here.
        // Settling it keeps a late rejection from surfacing as an unobserved
        // task exception.
        _ = SettleAsync(write);
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

            // Accumulation window (design §7.1's batching intent): a short batch
            // waits up to FlushInterval for more entries, so a light-volume trail
            // coalesces appends instead of paying one open/write/close per entry.
            // It never delays a full batch, and a 250ms window sits far inside
            // the change trail's 2s FlushTimeout.
            if (batch.Count < _options.FlushBatchSize)
            {
                await AccumulateAsync(reader, batch);
            }

            foreach (var group in batch.GroupBy(e => e.Entry.Trail))
            {
                await FlushAsync(group.Key, group.ToList());
            }
        }
    }

    /// <summary>
    /// Lets the batch in progress fill up to <see cref="AuditOptions.FlushBatchSize"/>
    /// within <see cref="AuditOptions.FlushInterval"/>. Entries arriving inside the
    /// window join this batch; the window ends the moment the batch fills, the
    /// channel closes, or the logger is disposed.
    /// </summary>
    private async Task AccumulateAsync(ChannelReader<PendingEntry> reader, List<PendingEntry> batch)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        budget.CancelAfter(_options.FlushInterval);
        try
        {
            while (batch.Count < _options.FlushBatchSize && await reader.WaitToReadAsync(budget.Token))
            {
                if (!reader.TryRead(out var item)) continue;
                batch.Add(item);
            }
        }
        catch (OperationCanceledException)
        {
            // The window elapsed or the logger is shutting down: flush what is
            // already in the batch rather than wait for more.
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

    /// <summary>
    /// Awaits an enqueue without letting it fail the caller: a rejected or failed
    /// write is swallowed fail-open (§14) and — being awaited or observed here —
    /// can never surface later as an unobserved task exception.
    /// </summary>
    private static async Task SettleAsync(Task pending)
    {
        try
        {
            await pending;
        }
        catch
        {
            // Fail-open, exactly as the flush path above.
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
