using System.ComponentModel;
using Eling.Backend.Bootstrap;
using Eling.Backend.Dtos;
using Eling.Core.Audit;
using Eling.Core.Codebase;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Tools;

/// <summary>
/// Reports codebase index stats. Pure read — never creates anything on disk.
/// Each call records one <c>codebase_status</c> access entry (amendment B).
/// </summary>
// NOTE: intentionally NOT an [McpServerToolType] anymore. Codebase status is
// now carried by memory_recall's `codebase` field, so exposing it as a
// standalone MCP tool only invited redundant calls. The class and method are
// retained for direct (in-process / test) use.
public sealed class CodebaseStatusTool
{
    private readonly CodebaseIndexService _svc;
    private readonly CodebaseWatcherService? _watcher;
    private readonly CodebaseAuditRecorder _audit;
    private readonly ILogger<CodebaseStatusTool>? _logger;

    public CodebaseStatusTool(
        CodebaseIndexService svc,
        CodebaseWatcherService? watcher = null,
        ILogger<CodebaseStatusTool>? logger = null,
        IAuditLogger? audit = null)
    {
        _svc = svc;
        _watcher = watcher;
        _audit = CodebaseAuditRecorder.Create(audit);
        _logger = logger;
    }

    [Description("Report codebase index stats: file/chunk counts, last indexed time, watcher state, and DB path. (Internal/fallback only — not exposed as an MCP tool; status is carried by memory_recall's `codebase` field.)")]
    public async Task<CodebaseStatusResponse> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var stats = await _svc.GetStatsAsync(cancellationToken);
            await _audit.RecordStatusAsync(_svc.ProjectRoot);
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
