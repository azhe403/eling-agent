using Eling.Application;

namespace Eling.Host;

public sealed class HttpCoordinatorMemoryChangeNotifier : IMemoryChangeNotifier
{
    private readonly int _port;
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(2) };

    public HttpCoordinatorMemoryChangeNotifier(int port)
    {
        _port = port;
    }

    public async Task NotifyAsync(string reason = "mutation", CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await HttpClient.PostAsync(
                $"http://127.0.0.1:{_port}/api/coordinator/notify-change",
                content: null,
                cancellationToken);
        }
        catch
        {
            // Dashboard is optional; fire-and-forget
        }
    }
}
