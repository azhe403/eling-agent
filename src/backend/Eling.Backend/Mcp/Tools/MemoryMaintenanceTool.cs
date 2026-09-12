using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eling.Core;
using Eling.Core.Memory;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Tools;

[McpServerToolType]
public sealed class MemoryMaintenanceTool
{
    private readonly IMemoryMaintenanceService _maintenance;
    private readonly IScopedMemoryService? _scoped;
    private readonly ILogger<MemoryMaintenanceTool>? _logger;

    public MemoryMaintenanceTool(IMemoryMaintenanceService maintenance, ILogger<MemoryMaintenanceTool>? logger = null, IScopedMemoryService? scoped = null)
    {
        _maintenance = maintenance;
        _scoped = scoped;
        _logger = logger;
    }

    [McpServerTool(Name = "memory_maintenance"), Description("Run on-demand memory maintenance (dedup, fuzzy merge, and cleanup of stale or corrupted memories). Default is dry-run mode.")]
    public async Task<string> RunMaintenanceAsync(
        [Description("Scope: project, global, or merged. Defaults to 'merged'.")] string scope = "merged",
        [Description("Detect only without applying changes. Defaults to true.")] bool dryRun = true,
        [Description("Array of operations to execute: 'dedup', 'merge', 'cleanup', 'reconcile'. Defaults to all.")] string[]? operations = null,
        [Description("Array of finding keys approved for risky operations (merge, cleanup). Ignored when dryRun=true.")] string[]? approveFindingIds = null,
        [Description("Similarity threshold in [0, 1] for fuzzy duplicate detection. Defaults to 0.8.")] double similarityThreshold = 0.8,
        [Description("Cutoff in days for stale archived/superseded memories to delete. Defaults to 90.")] int staleDays = 90,
        CancellationToken cancellationToken = default)
    {
        var maintenanceScope = scope?.ToLowerInvariant() switch
        {
            "project" => MaintenanceScope.Project,
            "global" => MaintenanceScope.Global,
            _ => MaintenanceScope.Merged
        };

        var ops = new HashSet<MaintenanceOperation>();
        if (operations is null || operations.Length == 0)
        {
            ops.Add(MaintenanceOperation.Dedup);
            ops.Add(MaintenanceOperation.Merge);
            ops.Add(MaintenanceOperation.Cleanup);
            ops.Add(MaintenanceOperation.Reconcile);
        }
        else
        {
            foreach (var op in operations)
            {
                if (Enum.TryParse<MaintenanceOperation>(op, ignoreCase: true, out var parsed))
                {
                    ops.Add(parsed);
                }
            }
        }

        var request = new MaintenanceRequest
        {
            Scope = maintenanceScope,
            DryRun = dryRun,
            Operations = ops,
            ApproveFindingIds = approveFindingIds is not null ? new HashSet<string>(approveFindingIds) : new(),
            SimilarityThreshold = similarityThreshold,
            StaleDays = staleDays
        };

        _logger?.LogInformation("Running memory maintenance (scope={Scope}, dryRun={DryRun})", scope, dryRun);
        var report = await _maintenance.RunAsync(request, cancellationToken);
        if (!dryRun && _scoped is not null)
        {
            await _scoped.RebuildIndexAsync(scope);
            _logger?.LogInformation("Index rebuilt after maintenance (scope={Scope})", scope);
        }
        return JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        });
    }
}
