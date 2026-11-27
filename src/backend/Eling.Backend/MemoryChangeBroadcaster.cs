using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Eling.Core;
using Eling.Core.Memory;

namespace Eling.Backend;

public sealed class MemoryChangeBroadcaster : IMemoryChangeNotifier
{
    private readonly object _lock = new();
    private readonly List<Channel<string>> _subscribers = [];

    public void Notify(string reason = "mutation") => Publish(reason);

    /// <summary>
    /// Publishes a scoped change as one compact JSON frame. The topic stays the
    /// shared one; workspace_id (and its root) travel in the payload so a
    /// dashboard filtered to one workspace can ignore events from the others.
    /// </summary>
    public void Notify(string reason, string workspaceId, string workspaceRoot)
        => Publish(JsonSerializer.Serialize(new
        {
            topic = reason,
            workspace_id = workspaceId,
            workspace = workspaceRoot,
        }));

    private void Publish(string payload)
    {
        lock (_lock)
        {
            foreach (var sub in _subscribers)
            {
                sub.Writer.TryWrite(payload);
            }
        }
    }

    public Task NotifyAsync(string reason = "mutation", CancellationToken cancellationToken = default)
    {
        Notify(reason);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<string> SubscribeAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
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
                while (channel.Reader.TryRead(out var item))
                {
                    yield return item;
                }
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
