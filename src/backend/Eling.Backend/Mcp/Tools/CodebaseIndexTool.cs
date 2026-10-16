using System.ComponentModel;
using System.Diagnostics;
using Eling.Backend.Dtos;
using Eling.Core.Audit;
using Eling.Core.Codebase;
using Eling.Core.Memory;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Tools;

/// <summary>
/// Build or refresh the codebase FTS index (incremental by default).
/// Thin wrapper over <see cref="CodebaseIndexService"/>: never blocks search,
/// single-flight via the service gate. Each explicit invocation records one
/// <c>codebase_index</c> change entry (design §8, amendment B); the background
/// watcher calls the service directly and is unrecorded by construction (§12).
/// </summary>
[McpServerToolType]
public sealed class CodebaseIndexTool
{
    private readonly CodebaseIndexService _svc;
    private readonly IMemoryChangeNotifier _notifier;
    private readonly CodebaseAuditRecorder _audit;
    private readonly ILogger<CodebaseIndexTool>? _logger;

    public CodebaseIndexTool(CodebaseIndexService svc, ILogger<CodebaseIndexTool>? logger = null, IMemoryChangeNotifier? notifier = null, IAuditLogger? audit = null)
    {
        _svc = svc;
        _logger = logger;
        _notifier = notifier ?? NullMemoryChangeNotifier.Instance;
        _audit = CodebaseAuditRecorder.Create(audit);
    }

    [McpServerTool(Name = "codebase_index"), Description("Build or refresh the codebase FTS index (incremental by default, cache in the global codebase store).")]
    public async Task<CodebaseIndexResponse> IndexAsync(
        [Description("true = rebuild all chunks, false = incremental by hash (default false).")] bool full = false,
        [Description("Optional scope, e.g. [\"src/backend\"]. Null = whole workspace.")] string[]? paths = null,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _svc.IndexAsync(full, paths, cancellationToken);
            sw.Stop();

            // A pass was already running: nothing was indexed, so report
            // fresh=false and surface the status instead of claiming success.
            // Still an explicit invocation, so it still records — with the
            // outcome the caller observed.
            if (result.Status is not null)
            {
                _logger?.LogInformation("codebase_index skipped: a pass is already running (status={Status}).", result.Status);
                await _audit.RecordIndexAsync(_svc.ProjectRoot, (int)sw.ElapsedMilliseconds);
                await _notifier.NotifyAsync("codebase", cancellationToken);
                return new CodebaseIndexResponse(
                    result.Files, result.Chunks, result.Skipped, result.Deleted,
                    sw.ElapsedMilliseconds, false, result.DbPath, result.Status);
            }

            _logger?.LogInformation(
                "codebase_index completed files={Files} chunks={Chunks} skipped={Skipped} deleted={Deleted} full={Full} tookMs={TookMs}",
                result.Files, result.Chunks, result.Skipped, result.Deleted, full, sw.ElapsedMilliseconds);
            await _audit.RecordIndexAsync(_svc.ProjectRoot, (int)sw.ElapsedMilliseconds);
            await _notifier.NotifyAsync("codebase", cancellationToken);
            return new CodebaseIndexResponse(result.Files, result.Chunks, result.Skipped, result.Deleted, sw.ElapsedMilliseconds, true, result.DbPath, null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger?.LogError(ex, "codebase_index failed (full={Full}).", full);
            await _audit.RecordIndexAsync(_svc.ProjectRoot, (int)sw.ElapsedMilliseconds, AuditOutcome.Error);
            throw;
        }
    }
}
