using System.Text;
using System.Text.Json;

using Eling.Core.Skill;

using Microsoft.Extensions.Logging;

namespace Eling.Backend.Services;

/// <summary>
/// Forwards this process's skill-catalog mutations to the dashboard owner
/// over HTTP, mirroring <c>Eling.Backend.Services.HttpCoordinatorCommitLeaseChangeNotifier</c>.
/// A peer backend serves no SSE at all, so its in-process broadcast has no
/// subscriber; this notifier is the only channel through which a skill change
/// made from one workspace reaches the dashboard served by another
/// workspace's eling. The relayed reason decides which SSE topic the owner
/// broadcasts (<c>skills</c>), so the skills page refreshes while the memory
/// page is not woken. Fire-and-forget: the dashboard is optional and never
/// blocks the caller. Lives in Backend; the skill service references only
/// <see cref="ISkillChangeNotifier"/> (the Core-side contract) so the service
/// never takes a dependency on the backend host. When this process owns the
/// port the POST lands on itself and still broadcasts, so peer takeover needs
/// no re-wiring.
/// </summary>
public sealed class HttpCoordinatorSkillChangeNotifier : ISkillChangeNotifier
{
    public const string SkillTopic = SkillChangeTopics.Skills;

    private readonly int _port;
    private readonly ILogger<HttpCoordinatorSkillChangeNotifier>? _logger;
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(2) };

    public HttpCoordinatorSkillChangeNotifier(int port, ILogger<HttpCoordinatorSkillChangeNotifier>? logger = null)
    {
        _port = port;
        _logger = logger;
    }

    public async Task NotifyAsync(string reason = "mutation", CancellationToken ct = default)
    {
        try
        {
            using var response = await HttpClient.PostAsync(
                $"http://127.0.0.1:{_port}/api/coordinator/notify-change",
                content: new StringContent(JsonSerializer.Serialize(SkillTopic), Encoding.UTF8, "application/json"),
                ct);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Skill change notification to dashboard owner on port {Port} failed; ignoring.", _port);
        }
    }
}
