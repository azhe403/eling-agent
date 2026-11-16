using Eling.Core.Skill.Scanning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Bootstrap;

/// <summary>
/// Runs one catalog reconciliation scan at host start, fire-and-forget:
/// <see cref="StartAsync"/> returns immediately and the scan runs in the
/// background so a large catalog or a locked index never blocks boot.
/// Individual bad folders are skipped by the scanner itself; any
/// infrastructure failure is logged as a warning and swallowed — the host
/// comes up on the existing index, and the dashboard's manual rescan can
/// retry. Rescanning is idempotent, so the machine-global hosts each
/// getting one scan at startup is harmless.
/// </summary>
public sealed class SkillCatalogScanHostedService : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<SkillCatalogScanHostedService>? _logger;
    private CancellationTokenSource? _cts;

    public SkillCatalogScanHostedService(
        IServiceProvider services,
        ILogger<SkillCatalogScanHostedService>? logger = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _cts.Token;
        _ = Task.Run(() => ScanSafeAsync(ct), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Deliberately not awaited: shutdown must not wait on the catalog
        // walk. The scan is best-effort; a cancelled scan simply stops
        // reporting.
        _cts?.Cancel();
        return Task.CompletedTask;
    }

    private async Task ScanSafeAsync(CancellationToken ct)
    {
        try
        {
            var scanner = _services.GetRequiredService<ISkillCatalogScanner>();
            var report = await scanner.ScanAsync();
            if (!ct.IsCancellationRequested)
            {
                _logger?.LogInformation(
                    "Skill catalog scan complete: {Discovered} discovered, {External} external adopted, {Missing} missing, {Failed} failed, {Renamed} renamed.",
                    report.Discovered, report.External, report.Missing, report.Failed, report.Renamed);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger?.LogWarning(ex, "Skill catalog scan failed on startup; continuing with the existing index.");
        }
    }
}
