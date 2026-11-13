using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Eling.Backend.Dtos;
using Eling.Core.CommitLease;

namespace Eling.Backend.Services;

/// <summary>
/// Push channel for the commit-lease panel, feeding
/// <c>/api/events/commit-lease</c>. Deliberately separate from
/// <see cref="MemoryChangeBroadcaster"/>: that one carries topic names on a
/// shared string channel several pages listen to, while this one carries full
/// snapshots, which is all a lease subscriber ever wants.
/// <para>
/// Event-driven, not pumped: a fresh snapshot reaches subscribers only when
/// something actually changed. The change signals are (1) the in-proc return
/// channel — this broadcaster is the standalone store's change notifier and
/// any caller may <see cref="NotifyAsync"/> after a mutation — and (2) the
/// coordinator bridge, which turns the <c>commit-lease</c> topic that
/// <c>HttpCoordinatorCommitLeaseChangeNotifier</c> relays onto
/// <c>/api/coordinator/notify-change</c> into a snapshot rebuild, starting
/// with the first subscriber and stopping with the last (like
/// <see cref="RuntimeMetricsBroadcaster"/>'s gate). In-proc changes made by
/// this process's shared store ride the same loopback relay with a known
/// dashboard port; the relayed frame is a full state transfer, so a duplicate
/// frame from the relay is indistinguishable from the original.
/// </para>
/// <para>
/// Every subscriber is handed the current snapshot as its first frame, so a
/// page that opens or reconnects resyncs immediately without polling.
/// </para>
/// </summary>
public sealed class CommitLeaseBroadcaster : ICommitLeaseChangeNotifier
{
    private readonly object _lock = new();
    private readonly List<Channel<CommitLeaseSnapshotDto>> _subscribers = [];
    private readonly Func<CancellationToken, Task<CommitLeaseSnapshotDto>> _snapshotFactory;
    private readonly MemoryChangeBroadcaster? _topics;
    private readonly ILogger<CommitLeaseBroadcaster> _logger;
    private CancellationTokenSource? _bridgeCts;

    public CommitLeaseBroadcaster(
        Func<CancellationToken, Task<CommitLeaseSnapshotDto>> snapshotFactory,
        ILogger<CommitLeaseBroadcaster> logger,
        MemoryChangeBroadcaster? topicsBridge = null)
    {
        ArgumentNullException.ThrowIfNull(snapshotFactory);
        _snapshotFactory = snapshotFactory;
        _logger = logger;
        _topics = topicsBridge;
    }

    /// <summary>Publishes a prebuilt snapshot to every current subscriber.</summary>
    public void Publish(CommitLeaseSnapshotDto snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_lock)
        {
            foreach (var subscriber in _subscribers)
                subscriber.Writer.TryWrite(snapshot);
        }
    }

    /// <summary>Rebuilds the snapshot from the store and publishes it.</summary>
    private async Task PublishCurrentAsync(CancellationToken cancellationToken)
    {
        try
        {
            Publish(await _snapshotFactory(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            // One failed rebuild must never kill the stream: the next change
            // retries. Warn, not debug - a lost frame is otherwise
            // indistinguishable from a quiet workspace.
            _logger.LogWarning(ex, "Lease snapshot rebuild failed; SSE subscribers not woken.");
        }
    }

    /// <summary>
    /// The store's change contract is "something in the file moved"; the frame
    /// content is always derived from the store at publish time.
    /// </summary>
    public Task NotifyAsync(CancellationToken cancellationToken = default)
        => PublishCurrentAsync(cancellationToken);

    /// <summary>Streams snapshots until <paramref name="cancellationToken"/> fires.</summary>
    public async IAsyncEnumerable<CommitLeaseSnapshotDto> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<CommitLeaseSnapshotDto>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        lock (_lock)
        {
            _subscribers.Add(channel);
            if (_subscribers.Count == 1 && _topics is not null) StartBridge();
        }

        try
        {
            // The resync frame is yielded from the enumerator itself rather
            // than written into the channel: it is this subscriber's own fact,
            // not a broadcast, and it must not starve behind (or invert) frames
            // already queued.
            CommitLeaseSnapshotDto? initial = null;
            try
            {
                initial = await _snapshotFactory(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A store that cannot serve a snapshot yields no frame here;
                // the next store change still reaches this subscriber through
                // the channel.
                _logger.LogWarning(ex, "Initial lease snapshot failed; live frames still flow.");
            }

            if (initial is not null) yield return initial;

            while (await channel.Reader.WaitToReadAsync(cancellationToken))
            {
                while (channel.Reader.TryRead(out var item))
                    yield return item;
            }
        }
        finally
        {
            lock (_lock)
            {
                _subscribers.Remove(channel);
                if (_subscribers.Count == 0) StopBridge();
            }
            channel.Writer.TryComplete();
        }
    }

    // Both callers hold _lock, which is also what makes the gate race-free:
    // a subscribe arriving after the last leave sees count 0 and restarts.
    private void StartBridge()
    {
        var cts = new CancellationTokenSource();
        _bridgeCts = cts;
        _ = Task.Run(() => ForwardTopicsAsync(cts.Token), CancellationToken.None);
    }

    private void StopBridge()
    {
        var cts = _bridgeCts;
        _bridgeCts = null;
        // Cancel only: the bridge may still be inside an await, and disposing
        // a CTS under it can surface ObjectDisposedException at the await point.
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException ex)
        {
            _logger.LogDebug(ex, "Lease coordinator bridge was already torn down.");
        }
    }

    private async Task ForwardTopicsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var topic in _topics!.SubscribeAsync(cancellationToken))
            {
                if (!string.Equals(topic, HttpCoordinatorCommitLeaseChangeNotifier.LeaseTopic, StringComparison.Ordinal))
                    continue;
                await PublishCurrentAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException ex)
        {
            // The last subscriber left; ending here is the design.
            _logger.LogDebug(ex, "Lease coordinator bridge ended on cancellation.");
        }
        catch (Exception ex)
        {
            // A dead bridge is logged, not swallowed silently; live frames
            // through NotifyAsync still work, and resubscribing restarts it.
            _logger.LogWarning(ex, "Lease coordinator bridge stopped unexpectedly.");
        }
    }
}
