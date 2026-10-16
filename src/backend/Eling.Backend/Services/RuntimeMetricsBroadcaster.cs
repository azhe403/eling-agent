using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Eling.Backend.Dtos;

namespace Eling.Backend.Services;

/// <summary>
/// Typed publish/subscribe for runtime metrics, feeding
/// <c>/api/events/runtime-metrics</c>. Deliberately separate from
/// <see cref="MemoryChangeBroadcaster"/>: that one carries topic names on a
/// shared string channel several pages listen to, while this one carries
/// payloads on its own cadence.
/// <para>
/// The sampling pump lives here and only here: it starts when the first
/// subscriber arrives and stops when the last one leaves. The only readers of
/// metric ticks are live dashboard streams, so sampling for nobody is work
/// with no consumer.
/// </para>
/// </summary>
public sealed class RuntimeMetricsBroadcaster
{
    private readonly object _lock = new();
    private readonly List<Channel<IReadOnlyList<RuntimeMetricsSnapshot>>> _subscribers = [];
    private readonly Func<IReadOnlyList<RuntimeMetricsSnapshot>> _sampleFactory;
    private readonly TimeSpan _interval;
    private readonly ILogger<RuntimeMetricsBroadcaster> _logger;
    private CancellationTokenSource? _pumpCts;

    public RuntimeMetricsBroadcaster(
        RuntimeMetricsSampler sampler,
        ILogger<RuntimeMetricsBroadcaster> logger,
        Func<IReadOnlyList<RuntimeMetricsSnapshot>>? sampleFactory = null,
        TimeSpan? interval = null)
    {
        // The factory seam keeps the pump testable without a registry of live
        // processes, and leaves production sampling a one-liner.
        _sampleFactory = sampleFactory ?? sampler.SampleAll;
        _logger = logger;
        _interval = interval ?? TimeSpan.FromSeconds(2);
    }

    /// <summary>Publishes a batch to every current subscriber.</summary>
    public void Publish(IReadOnlyList<RuntimeMetricsSnapshot> batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        lock (_lock)
        {
            foreach (var subscriber in _subscribers)
                subscriber.Writer.TryWrite(batch);
        }
    }

    /// <summary>Streams batches until <paramref name="cancellationToken"/> fires.</summary>
    public async IAsyncEnumerable<IReadOnlyList<RuntimeMetricsSnapshot>> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<IReadOnlyList<RuntimeMetricsSnapshot>>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        lock (_lock)
        {
            _subscribers.Add(channel);
            if (_subscribers.Count == 1) StartPump();
        }

        try
        {
            while (await channel.Reader.WaitToReadAsync(cancellationToken))
            {
                while (channel.Reader.TryRead(out var batch))
                    yield return batch;
            }
        }
        finally
        {
            lock (_lock)
            {
                _subscribers.Remove(channel);
                if (_subscribers.Count == 0) StopPump();
            }
            channel.Writer.TryComplete();
        }
    }

    // Both callers hold _lock, which is also what makes the gate race-free:
    // a subscribe arriving after the last leave sees count 0 and restarts.
    private void StartPump()
    {
        var cts = new CancellationTokenSource();
        _pumpCts = cts;
        _ = Task.Run(() => PumpAsync(cts.Token), CancellationToken.None);
    }

    private void StopPump()
    {
        var cts = _pumpCts;
        _pumpCts = null;
        // Cancel only: the pump may still be inside an await, and disposing a
        // CTS under it can surface ObjectDisposedException at the await point.
        // CTS is safe to leave for the finalizer.
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            /* already torn down */
        }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            // One immediate sample on subscribe so the first frame is not a
            // full interval behind the page that just opened.
            PublishTick();
            while (await timer.WaitForNextTickAsync(ct))
                PublishTick();
        }
        catch (OperationCanceledException)
        {
            // The last subscriber left; ending here is the design.
        }
        finally
        {
            _logger.LogDebug("Runtime metrics pump stopped");
        }
    }

    private void PublishTick()
    {
        try
        {
            Publish(_sampleFactory());
        }
        catch (Exception ex)
        {
            // One failed tick must never kill the stream: the next tick
            // retries, and the on-demand runtimes endpoint remains the
            // fallback a consumer can always read. Warn, not debug: a lost
            // batch is otherwise indistinguishable from a quiet workspace.
            _logger.LogWarning(ex, "Runtime metrics tick failed");
        }
    }
}
