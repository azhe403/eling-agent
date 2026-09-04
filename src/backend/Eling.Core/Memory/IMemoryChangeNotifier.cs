namespace Eling.Core;

public interface IMemoryChangeNotifier
{
    Task NotifyAsync(string reason = "mutation", CancellationToken cancellationToken = default);
}

public sealed class NullMemoryChangeNotifier : IMemoryChangeNotifier
{
    public static readonly NullMemoryChangeNotifier Instance = new();

    public Task NotifyAsync(string reason = "mutation", CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

