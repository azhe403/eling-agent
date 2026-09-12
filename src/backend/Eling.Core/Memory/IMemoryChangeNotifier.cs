namespace Eling.Core.Memory;

public interface IMemoryChangeNotifier
{
    Task NotifyAsync(string reason = "mutation", CancellationToken cancellationToken = default);
}

