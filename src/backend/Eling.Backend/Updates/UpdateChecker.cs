using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Updates;

/// <summary>
/// Orchestrates the update check: serves the cache without network traffic and
/// refreshes it only when stale. An update is offered only when the newest
/// release can be proven newer than the running version, so an unresolvable or
/// unstamped version never produces a false offer.
/// </summary>
public sealed class UpdateChecker : IUpdateChecker, IDisposable
{
    /// <summary>Shortest gap between two real checks, shared with the pump.</summary>
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromHours(12);

    /// <summary>Per-request timeout, shared by every host that registers the client.</summary>
    internal static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(10);

    private readonly GitHubReleaseClient _client;
    private readonly FileUpdateCache _cache;
    private readonly ILogger<UpdateChecker> _logger;
    private readonly string _currentVersion;
    private readonly string _channel;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public UpdateChecker(
        GitHubReleaseClient client,
        FileUpdateCache cache,
        ILogger<UpdateChecker> logger,
        string currentVersion,
        string channel = "prerelease")
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(currentVersion);
        ArgumentNullException.ThrowIfNull(channel);

        _client = client;
        _cache = cache;
        _logger = logger;
        _currentVersion = currentVersion;
        _channel = channel;
    }

    /// <summary>Version stamped into the running assembly, empty when unresolvable.</summary>
    public static string ResolveCurrentVersion()
        => typeof(UpdateChecker).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? string.Empty;

    /// <summary>Whether the operator disabled update checks via environment.</summary>
    public static bool IsDisabled()
    {
        var value = Environment.GetEnvironmentVariable("ELING_DISABLE_UPDATE_CHECK");
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public Task<UpdateStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (IsDisabled())
        {
            return Task.FromResult(DisabledStatus());
        }

        return Task.FromResult(_cache.GetStatus() ?? UnknownStatus());
    }

    /// <inheritdoc />
    public async Task<UpdateStatus> CheckNowAsync(CancellationToken cancellationToken = default)
    {
        if (IsDisabled())
        {
            return DisabledStatus();
        }

        if (string.IsNullOrWhiteSpace(_currentVersion))
        {
            return _cache.GetStatus() ?? UnknownStatus();
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var cached = _cache.GetStatus();
            if (cached is not null && DateTimeOffset.UtcNow - cached.CheckedAt < CheckInterval)
            {
                return cached;
            }

            var candidate = await _client.GetLatestAsync(cancellationToken);
            if (candidate is null)
            {
                return cached ?? UnknownStatus();
            }

            var status = new UpdateStatus
            {
                CurrentVersion = _currentVersion,
                LatestVersion = candidate.Tag,
                UpdateAvailable = SemanticVersionCompare.IsNewer(candidate.Tag, _currentVersion),
                ReleaseUrl = candidate.Url,
                ReleaseNotes = candidate.Notes,
                CheckedAt = DateTimeOffset.UtcNow,
                Channel = _channel,
            };
            _cache.SaveStatus(status);
            return status;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private UpdateStatus UnknownStatus()
        => new()
        {
            CurrentVersion = _currentVersion,
            CheckedAt = DateTimeOffset.UtcNow,
            Channel = _channel,
        };

    private UpdateStatus DisabledStatus()
        => new()
        {
            CurrentVersion = _currentVersion,
            CheckedAt = DateTimeOffset.UtcNow,
            Channel = "disabled",
        };
}
