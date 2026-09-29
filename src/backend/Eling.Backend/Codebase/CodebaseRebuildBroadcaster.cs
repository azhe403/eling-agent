using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Eling.Backend.Dtos;

namespace Eling.Backend.Codebase;

/// <summary>
/// Typed publish/subscribe for codebase rebuild progress, feeding
/// <c>/api/events/codebase-rebuild</c>. Deliberately separate from
/// <see cref="MemoryChangeBroadcaster"/>: that one carries topic names on a
/// <see cref="string"/> channel and is shared with the memory/coordinator
/// SSE stream, so a typed payload here needs no change to that contract.
/// Subscribers that arrive late simply miss earlier snapshots — the job
/// status endpoint remains the source of truth.
/// </summary>
public sealed class CodebaseRebuildBroadcaster
{
    private readonly object _lock = new();
    private readonly List<Channel<CodebaseRebuildProgress>> _subscribers = [];

    /// <summary>Publishes a snapshot to every current subscriber.</summary>
    public void Publish(CodebaseRebuildProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        lock (_lock)
        {
            foreach (var subscriber in _subscribers)
                subscriber.Writer.TryWrite(progress);
        }
    }

    /// <summary>Streams snapshots until <paramref name="cancellationToken"/> fires.</summary>
    public async IAsyncEnumerable<CodebaseRebuildProgress> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<CodebaseRebuildProgress>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        lock (_lock)
        {
            _subscribers.Add(channel);
        }

        try
        {
            while (await channel.Reader.WaitToReadAsync(cancellationToken))
            {
                while (channel.Reader.TryRead(out var snapshot))
                    yield return snapshot;
            }
        }
        finally
        {
            lock (_lock)
            {
                _subscribers.Remove(channel);
            }
            channel.Writer.TryComplete();
        }
    }
}
