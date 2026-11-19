using System.Text;
using System.Text.Json;
using Eling.Core;
using Eling.Core.Memory;
using Microsoft.Extensions.Logging;

namespace Eling.Backend;

/// <summary>
/// Forwards this process's mutation notifications to the dashboard owner over
/// HTTP. A peer backend serves no SSE at all, so its in-process
/// <see cref="MemoryChangeBroadcaster"/> has no subscriber; this notifier is
/// the only channel through which a write made from one workspace reaches the
/// dashboard served by another workspace's eling. The <paramref name="reason"/>
/// travels with the request so the owner relays it on the correct topic
/// (memory traffic is "coordinator", codebase traffic keeps "codebase").
/// Fire-and-forget: the dashboard is optional and never blocks the caller.
/// </summary>
public sealed class HttpCoordinatorMemoryChangeNotifier : IMemoryChangeNotifier
{
    private readonly int _port;
    private readonly ILogger<HttpCoordinatorMemoryChangeNotifier>? _logger;
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(2) };

    public HttpCoordinatorMemoryChangeNotifier(int port, ILogger<HttpCoordinatorMemoryChangeNotifier>? logger = null)
    {
        _port = port;
        _logger = logger;
    }

    public async Task NotifyAsync(string reason = "mutation", CancellationToken cancellationToken = default)
    {
        try
        {
            using var content = string.IsNullOrWhiteSpace(reason)
                ? null
                : new StringContent(JsonSerializer.Serialize(reason), Encoding.UTF8, "application/json");
            using var response = await HttpClient.PostAsync(
                $"http://127.0.0.1:{_port}/api/coordinator/notify-change",
                content: content,
                cancellationToken);
        }
        catch (Exception ex)
        {
            // Dashboard is optional; fire-and-forget
            _logger?.LogDebug(ex, "Dashboard notify failed (owner offline?) for port {Port}", _port);
        }
    }
}
