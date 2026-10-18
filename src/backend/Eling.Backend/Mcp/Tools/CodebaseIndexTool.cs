using System.ComponentModel;
using System.Diagnostics;
using Eling.Backend.Dtos;
using Eling.Core.Audit;
using Eling.Core.Codebase;
using Eling.Core.Memory;
using Eling.Core.Projects;
using Eling.Core.Scope;
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
    private readonly IWorkspacesRegistry? _workspaces;
    private readonly ILoggerFactory? _loggerFactory;

    public CodebaseIndexTool(
        CodebaseIndexService svc,
        ILogger<CodebaseIndexTool>? logger = null,
        IMemoryChangeNotifier? notifier = null,
        IWorkspacesRegistry? workspaces = null,
        ILoggerFactory? loggerFactory = null,
        IAuditLogger? audit = null)
    {
        _svc = svc;
        _logger = logger;
        _notifier = notifier ?? NullMemoryChangeNotifier.Instance;
        _workspaces = workspaces;
        _loggerFactory = loggerFactory;
        _audit = CodebaseAuditRecorder.Create(audit);
    }

    [McpServerTool(Name = "codebase_index"), Description("Build or refresh the codebase FTS index (incremental by default, cache in the global codebase store).")]
    public async Task<CodebaseIndexResponse> IndexAsync(
        [Description("true = rebuild all chunks, false = incremental by hash (default false).")] bool full = false,
        [Description("Optional scope, e.g. [\"src/backend\"]. Null = whole workspace.")] string[]? paths = null,
        [Description("Optional absolute directory path to index as an external workspace root. Null = current workspace.")] string? root = null,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // External root: index into its own dedicated DB so ownership stays
            // with that folder instead of polluting the current workspace.
            if (!string.IsNullOrWhiteSpace(root))
            {
                var validated = ElingPaths.EnsureCodebaseRootsAllowed(new[] { root! });
                var normalized = validated.Count > 0
                    ? validated[0]
                    : Path.GetFullPath(root!).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (!Directory.Exists(normalized))
                    throw new DirectoryNotFoundException($"Directory not found: '{normalized}'.");

                if (!string.Equals(normalized, _svc.ProjectRoot, StringComparison.OrdinalIgnoreCase))
                {
                    var dbPath = ElingPaths.ResolveCodebaseDbPath(normalized);
                    using var index = new SqliteCodebaseIndex(dbPath);
                    var svc = new CodebaseIndexService(normalized, index, _loggerFactory?.CreateLogger<CodebaseIndexService>());
                    var result = await svc.IndexAsync(full, paths, cancellationToken);
                    sw.Stop();

                    if (result.Status is not null)
                    {
                        _logger?.LogInformation("codebase_index skipped: a pass is already running (status={Status}).", result.Status);
                        await _audit.RecordIndexAsync(normalized, (int)sw.ElapsedMilliseconds);
                        await _notifier.NotifyAsync("codebase", cancellationToken);
                        return new CodebaseIndexResponse(
                            result.Files, result.Chunks, result.Skipped, result.Deleted,
                            sw.ElapsedMilliseconds, false, result.DbPath, result.Status);
                    }

                    _logger?.LogInformation(
                        "codebase_index completed files={Files} chunks={Chunks} skipped={Skipped} deleted={Deleted} full={Full} tookMs={TookMs}",
                        result.Files, result.Chunks, result.Skipped, result.Deleted, full, sw.ElapsedMilliseconds);

                    var now = DateTimeOffset.UtcNow;
                    _workspaces?.Record(new RegisteredWorkspace(
                        string.Empty,
                        normalized,
                        normalized,
                        CodebaseEnabled: true,
                        FirstSeenAt: now,
                        LastSeenAt: now));

                    await _audit.RecordIndexAsync(normalized, (int)sw.ElapsedMilliseconds);
                    await _notifier.NotifyAsync("codebase", cancellationToken);
                    return new CodebaseIndexResponse(result.Files, result.Chunks, result.Skipped, result.Deleted, sw.ElapsedMilliseconds, true, result.DbPath, null);
                }

                // Same as current workspace: fall through to the default path.
                root = null;
            }

            var localResult = await _svc.IndexAsync(full, paths, cancellationToken);
            sw.Stop();

            if (localResult.Status is not null)
            {
                _logger?.LogInformation("codebase_index skipped: a pass is already running (status={Status}).", localResult.Status);
                await _audit.RecordIndexAsync(_svc.ProjectRoot, (int)sw.ElapsedMilliseconds);
                await _notifier.NotifyAsync("codebase", cancellationToken);
                return new CodebaseIndexResponse(
                    localResult.Files, localResult.Chunks, localResult.Skipped, localResult.Deleted,
                    sw.ElapsedMilliseconds, false, localResult.DbPath, localResult.Status);
            }

            _logger?.LogInformation(
                "codebase_index completed files={Files} chunks={Chunks} skipped={Skipped} deleted={Deleted} full={Full} tookMs={TookMs}",
                localResult.Files, localResult.Chunks, localResult.Skipped, localResult.Deleted, full, sw.ElapsedMilliseconds);
            await _audit.RecordIndexAsync(_svc.ProjectRoot, (int)sw.ElapsedMilliseconds);
            await _notifier.NotifyAsync("codebase", cancellationToken);
            return new CodebaseIndexResponse(localResult.Files, localResult.Chunks, localResult.Skipped, localResult.Deleted, sw.ElapsedMilliseconds, true, localResult.DbPath, null);
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