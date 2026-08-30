using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Eling.Application;

namespace Eling.Dashboard;

public sealed class MemoryChangeBroadcaster : IMemoryChangeNotifier
{
    private readonly object _lock = new();
    private readonly List<Channel<string>> _subscribers = [];

    public void Notify(string reason = "mutation")
    {
        lock (_lock)
        {
            foreach (var sub in _subscribers)
            {
                sub.Writer.TryWrite(reason);
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
