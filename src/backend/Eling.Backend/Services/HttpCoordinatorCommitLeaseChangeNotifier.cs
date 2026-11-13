using System.Text;
using System.Text.Json;

using Eling.Core.CommitLease;

using Microsoft.Extensions.Logging;

namespace Eling.Backend.Services;

/// <summary>
/// Forwards this process's lease mutations to the dashboard owner over HTTP,
/// mirroring <c>Eling.Backend.HttpCoordinatorMemoryChangeNotifier</c>. A peer
/// backend serves no SSE at all, so its in-process broadcast has no
/// subscriber; this notifier is the only channel through which a lease change
/// made from one workspace reaches the dashboard served by another
/// workspace's eling. The relayed reason decides which SSE topic the owner
/// broadcasts (<c>commit-lease</c>), so the lease panel refreshes while the
/// memory page is not woken. Fire-and-forget: the dashboard is optional and
/// never blocks the caller. Lives in Backend; the lease store references only
/// <see cref="ICommitLeaseChangeNotifier"/> (the Core-side contract) so the
/// store never takes a dependency on the backend host.
/// </summary>
public sealed class HttpCoordinatorCommitLeaseChangeNotifier : ICommitLeaseChangeNotifier
{
    public const string LeaseTopic = "commit-lease";

    private readonly int _port;
    private readonly ILogger<HttpCoordinatorCommitLeaseChangeNotifier>? _logger;
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(2) };

    public HttpCoordinatorCommitLeaseChangeNotifier(int port, ILogger<HttpCoordinatorCommitLeaseChangeNotifier>? logger = null)
    {
        _port = port;
        _logger = logger;
    }

    public async Task NotifyAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await HttpClient.PostAsync(
                $"http://127.0.0.1:{_port}/api/coordinator/notify-change",
                content: new StringContent(JsonSerializer.Serialize(LeaseTopic), Encoding.UTF8, "application/json"),
                ct);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Lease change notification to dashboard owner on port {Port} failed; ignoring.", _port);
        }
    }
}
