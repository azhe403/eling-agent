using Eling.Backend.Bootstrap;
using Eling.Backend.Dtos;
using Eling.Backend.Services;
using Eling.Core.Git;
using Eling.Core.Runtime;
using Eling.Core.Scope;
using Eling.Core.Workspaces;
using Microsoft.AspNetCore.Mvc;

namespace Eling.Backend.Controllers;

/// <summary>
/// The runtime list the dashboard's codebase picker reads.
/// </summary>
/// <remarks>
/// A controller rather than a minimal-API handler because the coordinator group
/// is being migrated off minimal APIs, and new routes should not extend the
/// style being retired. Only <c>GET /runtimes</c> lives here so far; the other
/// coordinator routes remain in <c>Endpoints/CoordinatorEndpoints.cs</c> and are
/// deliberately untouched.
/// <para>
/// The two modes exist because the subdomains need different answers, and
/// forcing one answer on both was the bug:
/// <list type="bullet">
/// <item>
/// default — live processes only. A memory service used to be resolved this way,
/// which hid any scope nobody happened to be running in.
/// </item>
/// <item>
/// <c>includeRegistered</c> — every workspace that has ever registered, including
/// closed ones. Codebase needs this: its indexes are read-only files in a global
/// store and stay readable after the owning process exits.
/// </item>
/// </list>
/// Memory's own picker does not read this endpoint at all; it reads
/// <c>/api/project/scopes</c>, which resolves the <c>.eling</c> chain.
/// </para>
/// </remarks>
[ApiController]
[Route("api/coordinator")]
public sealed class CoordinatorController(
    RuntimeRegistry registry,
    MemoryChangeBroadcaster broadcaster,
    WorkspaceExpansionStore expansionStore,
    ProcessMetricsTracker? metricsTracker = null) : ControllerBase
{
    /// <summary>
    /// Registered runtimes. With <paramref name="includeRegistered"/> the result
    /// also contains workspaces whose process has exited; those carry
    /// <c>processId = 0</c> and <c>isAlive = false</c>, and otherwise have the
    /// same shape as a live entry, so consumers need no special case.
    /// <para>
    /// With <paramref name="allProcesses"/> the live list is no longer deduped
    /// to one row per workspace: several stdio sessions can run in one folder
    /// (one per opencode/openchamber session, plus peers and the desktop app),
    /// and the dashboard aggregates or lists them instead of showing a single
    /// representative process. Remembered rows still appear only for roots no
    /// live process covers.
    /// </para>
    /// </summary>
    /// <summary>
    /// The workspace tree's open/closed shape, so the desktop app and the
    /// dashboard render the same tree. A missing store answers with an empty
    /// shape, which means "every node keeps its default".
    /// </summary>
    [HttpGet("workspaces/expanded")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<WorkspaceExpansionDto> GetWorkspaceExpansion()
    {
        var expansion = expansionStore.Load();
        return Ok(new WorkspaceExpansionDto(expansion.Expanded, expansion.Collapsed));
    }

    /// <summary>
    /// Replaces the workspace tree's open/closed shape. Both clients write here
    /// after a toggle, so the other one picks the same shape up on its next
    /// read. Answers <c>204</c> once the shape is stored.
    /// </summary>
    [HttpPut("workspaces/expanded")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult SetWorkspaceExpansion([FromBody] WorkspaceExpansionDto request)
    {
        expansionStore.Save(new WorkspaceExpansion(
            request.Expanded ?? [],
            request.Collapsed ?? []));
        return NoContent();
    }

    [HttpGet("runtimes")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyCollection<RuntimeInfoDto>>> GetRuntimesAsync(
        [FromQuery] bool includeRegistered = false,
        [FromQuery] bool allProcesses = false,
        CancellationToken cancellationToken = default)
    {
        var runtimes = includeRegistered
            ? await registry.AliveOrRegisteredAsync(cancellationToken, allProcesses)
            : registry.Alive(allProcesses);

        var dtos = runtimes.Select(r =>
        {
            var isOwner = r.ProcessId > 0 && r.ProcessId == Environment.ProcessId;
            var dto = RuntimeInfoDto.From(r) with
            {
                IsDashboardOwner = isOwner,
                DashboardPort = isOwner ? DashboardPort.Resolve() : null,
                RepositoryRoot = ResolveRepositoryRoot(r),
            };
            if (r.IsAlive && r.ProcessId > 0 && metricsTracker is not null)
            {
                var metrics = metricsTracker.Sample(r.ProcessId);
                if (metrics is not null)
                {
                    dto = dto with
                    {
                        MemoryBytes = metrics.MemoryBytes,
                        CpuPercent = metrics.CpuPercent
                    };
                }
            }
            return dto;
        }).ToList();

        return Ok(dtos);
    }

    /// <summary>
    /// The main worktree root of the repository a runtime works in, or null when
    /// the workspace is not inside a git repository.
    /// </summary>
    /// <remarks>
    /// Read from the working copy's own metadata rather than by running git: the
    /// runtime list is fetched on every dashboard page load, and one process per
    /// workspace would cost more than the answer is worth. The user-home
    /// sentinel is not a path, so it resolves to nothing.
    /// </remarks>
    private static string? ResolveRepositoryRoot(RuntimeInfo runtime)
    {
        var root = runtime.CodebaseRoot();
        if (string.IsNullOrWhiteSpace(root) || root == "UserScope") return null;

        return GitRepositoryResolver.ResolveRepositoryRoot(root);
    }

    /// <summary>
    /// Batch read of codebase status for all known projects. Read on-the-fly from disk
    /// in a single request without modifying or writing any database.
    /// </summary>
    [HttpGet("projects/codebase-statuses")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyCollection<ProjectCodebaseStatusDto>>> GetProjectCodebaseStatusesAsync(
        CancellationToken cancellationToken = default)
    {
        var runtimes = await registry.AliveOrRegisteredAsync(cancellationToken);
        var result = new List<ProjectCodebaseStatusDto>();

        foreach (var r in runtimes)
        {
            var root = r.WorkspaceRoot;
            if (string.IsNullOrWhiteSpace(root) || root == "UserScope") continue;
            var normalized = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var dbPath = ElingPaths.ResolveCodebaseDbPath(normalized);

            // The size comes from the file, not from the index: a missing or
            // corrupt database still occupies disk, and what the number is for
            // is what the workspace costs on disk.
            var dbSize = Core.Codebase.CodebaseIndexSize.Measure(dbPath);

            if (!System.IO.File.Exists(dbPath))
            {
                result.Add(new ProjectCodebaseStatusDto(normalized, 0, 0, null, dbPath, dbSize));
                continue;
            }

            try
            {
                using var index = new Core.Codebase.SqliteCodebaseIndex(dbPath, readOnly: true);
                var stats = await index.GetStatsAsync(cancellationToken);
                result.Add(new ProjectCodebaseStatusDto(normalized, stats.FileCount, stats.ChunkCount, stats.LastIndexedAt, dbPath, dbSize));
            }
            catch
            {
                // Unreadable/corrupt index: fall back to zero-count DTO so one bad DB never fails the batch.
                result.Add(new ProjectCodebaseStatusDto(normalized, 0, 0, null, dbPath, dbSize));
            }
        }

        return Ok(result);
    }

    /// <summary>
    /// Deletes a workspace's components, each one opt-in-able: the registry row,
    /// the codebase index, and the local .eling data folder. Keeping the registry row
    /// while clearing the rest is valid — the workspace stays listed so it can
    /// be re-indexed later.
    /// </summary>
    [HttpPost("project/delete")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> DeleteProjectAsync(
        [FromBody] DeleteProjectRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request?.WorkspaceRoot))
        {
            return BadRequest("WorkspaceRoot is required.");
        }

        var normalized = Path.GetFullPath(request.WorkspaceRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // 1. Delete from workspaces database and unregister if active —
        //    one component of the delete, not its mandatory core.
        if (request.DeleteRegistry)
        {
            await registry.DeleteWorkspaceAsync(normalized, cancellationToken);
        }

        // 2. Optionally delete codebase index db and sidecars (-wal, -shm)
        if (request.DeleteCodebaseIndex)
        {
            try
            {
                var dbPath = ElingPaths.ResolveCodebaseDbPath(normalized);
                DeleteDatabaseAndSidecars(dbPath);
            }
            catch
            {
                // Best-effort index deletion
            }
        }

        // 3. Optionally delete .eling directory in workspace
        if (request.DeleteDotEling)
        {
            try
            {
                var dotEling = Path.Combine(normalized, ElingPaths.DotElingDirName);
                if (Directory.Exists(dotEling))
                {
                    Directory.Delete(dotEling, recursive: true);
                }
            }
            catch
            {
                // Best-effort dotEling deletion
            }
        }

        broadcaster.Notify("runtimes");
        broadcaster.Notify("codebase");

        return Ok(new { success = true, workspaceRoot = normalized });
    }

    /// <summary>
    /// Deletes a SQLite database and its WAL sidecar files (-wal, -shm, -journal).
    /// Leaving sidecars behind causes SQLite to open a stale or corrupt DB on next access.
    /// </summary>
    private static void DeleteDatabaseAndSidecars(string dbPath)
    {
        if (System.IO.File.Exists(dbPath))
            System.IO.File.Delete(dbPath);

        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            var sidecarPath = dbPath + suffix;
            if (System.IO.File.Exists(sidecarPath))
                System.IO.File.Delete(sidecarPath);
        }
    }

    /// <summary>
    /// The workspace list, arranged one way per request. <paramref name="group"/>
    /// picks the arrangement (<c>flat</c>, <c>repo</c>, or <c>folder</c>) and the
    /// answer carries the deduplicated rows plus that single arrangement, so
    /// switching the view is a fresh request rather than a client-side regroup.
    /// <para>
    /// <paramref name="status"/> and <paramref name="q"/> filter before the
    /// arrangement is built, so a filtered list is still a server-computed tree.
    /// <paramref name="q"/> matches the workspace name, its root, or any owning
    /// process id.
    /// </para>
    /// </summary>
    [HttpGet("workspaces")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<WorkspaceTreesDto>> GetWorkspacesAsync(
        [FromQuery] string? group,
        [FromQuery] string? status = null,
        [FromQuery] string? q = null,
        CancellationToken cancellationToken = default)
    {
        if (!WorkspaceGroupModes.TryParse(group, out var mode))
        {
            return BadRequest(new { error = $"Invalid group value '{group}'. Expected 'flat', 'repo', or 'folder'." });
        }

        var normalizedStatus = status?.Trim().ToLowerInvariant() ?? "all";
        if (normalizedStatus is not ("all" or "active" or "offline"))
        {
            return BadRequest(new { error = $"Invalid status value '{status}'. Expected 'all', 'active', or 'offline'." });
        }

        var runtimes = await registry.AliveOrRegisteredAsync(cancellationToken, allProcesses: true);

        var rows = RowsOf(runtimes);

        var trimmedQuery = q?.Trim();
        if (!string.IsNullOrEmpty(trimmedQuery))
        {
            var query = trimmedQuery;
            rows = rows.Where(row =>
            {
                var nameMatch = row.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
                var rootMatch = row.Root.Contains(query, StringComparison.OrdinalIgnoreCase);
                var pidMatches = row.ProcessIds.Any(pid => pid.ToString().Contains(query, StringComparison.OrdinalIgnoreCase));
                return nameMatch || rootMatch || pidMatches;
            }).ToList();
        }

        if (normalizedStatus == "active")
        {
            rows = rows.Where(r => r.Alive).ToList();
        }
        else if (normalizedStatus == "offline")
        {
            rows = rows.Where(r => !r.Alive).ToList();
        }

        var coreRows = rows.Select(r => new WorkspaceTreeRowDto(
            r.Root, r.Name, r.RepositoryRoot, r.Alive, r.ProcessCount)).ToList();
        return Ok(WorkspaceTreeDtoMapper.Map(coreRows, mode));
    }

    private sealed record WorkspaceRow(
        string Root,
        string Name,
        string? RepositoryRoot,
        bool Alive,
        int ProcessCount,
        IReadOnlyList<int> ProcessIds);

    private static List<WorkspaceRow> RowsOf(IReadOnlyList<RuntimeInfo> runtimes)
    {
        var byRoot = new Dictionary<string, List<RuntimeInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var runtime in runtimes)
        {
            var root = runtime.CodebaseRoot();
            if (string.IsNullOrWhiteSpace(root) || root == "UserScope") continue;
            if (!runtime.CodebaseEnabled) continue;

            if (!byRoot.TryGetValue(root, out var list))
            {
                list = [];
                byRoot[root] = list;
            }
            list.Add(runtime);
        }

        var result = new List<WorkspaceRow>();
        foreach (var entry in byRoot)
        {
            var root = entry.Key;
            var group = entry.Value;
            var repoRoot = ResolveRepositoryRoot(group[0]);
            var alive = group.Any(r => r.IsAlive);
            var processIds = group.Where(r => r.ProcessId > 0).Select(r => r.ProcessId).ToList();
            var trimmed = root.TrimEnd('\\', '/');
            var name = System.IO.Path.GetFileName(trimmed);
            result.Add(new WorkspaceRow(
                root,
                string.IsNullOrEmpty(name) ? root : name,
                repoRoot,
                alive,
                processIds.Count,
                processIds));
        }
        return result;
    }
}
