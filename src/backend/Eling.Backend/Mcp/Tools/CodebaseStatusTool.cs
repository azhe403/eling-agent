using System.ComponentModel;
using Eling.Backend.Bootstrap;
using Eling.Backend.Dtos;
using Eling.Core.Codebase;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Tools;

/// <summary>
/// Reports codebase index stats. Pure read — never creates anything on disk.
/// </summary>
[McpServerToolType]
public sealed class CodebaseStatusTool
{
    private readonly CodebaseIndexService _svc;
    private readonly CodebaseWatcherService? _watcher;
    private readonly ILogger<CodebaseStatusTool>? _logger;

    public CodebaseStatusTool(
        CodebaseIndexService svc,
        CodebaseWatcherService? watcher = null,
        ILogger<CodebaseStatusTool>? logger = null)
    {
        _svc = svc;
        _watcher = watcher;
        _logger = logger;
    }

    [McpServerTool(Name = "codebase_status"), Description("Report codebase index stats: file/chunk counts, last indexed time, watcher state, and DB path.")]
    public async Task<CodebaseStatusResponse> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var stats = await _svc.GetStatsAsync(cancellationToken);
            return new CodebaseStatusResponse(
                _svc.ProjectRoot,
                stats.LastIndexedAt,
                stats.FileCount,
                stats.ChunkCount,
                WatcherActive: _watcher?.IsActive ?? false,
                PendingFiles: 0,
                stats.DbPath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "codebase_status failed.");
            throw;
        }
    }
}
