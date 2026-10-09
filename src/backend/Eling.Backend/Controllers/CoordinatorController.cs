using Eling.Backend.Dtos;
using Eling.Backend.Services;
using Eling.Core.Scope;
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
    ProcessMetricsTracker? metricsTracker = null) : ControllerBase
{
    /// <summary>
    /// Registered runtimes. With <paramref name="includeRegistered"/> the result
    /// also contains workspaces whose process has exited; those carry
    /// <c>processId = 0</c> and <c>isAlive = false</c>, and otherwise have the
    /// same shape as a live entry, so consumers need no special case.
    /// </summary>
    [HttpGet("runtimes")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyCollection<RuntimeInfoDto>>> GetRuntimesAsync(
        [FromQuery] bool includeRegistered = false,
        CancellationToken cancellationToken = default)
    {
        var runtimes = includeRegistered
            ? await registry.AliveOrRegisteredAsync(cancellationToken)
            : registry.Alive();

        var dtos = runtimes.Select(r =>
        {
            var dto = RuntimeInfoDto.From(r);
            if (r.IsAlive && r.ProcessId > 0 && metricsTracker is not null)
            {
                var metrics = metricsTracker.Sample(r.ProcessId);
                if (metrics is not null)
                {
                    return dto with
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

            if (!System.IO.File.Exists(dbPath))
            {
                result.Add(new ProjectCodebaseStatusDto(normalized, 0, 0, null, dbPath));
                continue;
            }

            try
            {
                using var index = new Core.Codebase.SqliteCodebaseIndex(dbPath, readOnly: true);
                var stats = await index.GetStatsAsync(cancellationToken);
                result.Add(new ProjectCodebaseStatusDto(normalized, stats.FileCount, stats.ChunkCount, stats.LastIndexedAt, dbPath));
            }
            catch
            {
                result.Add(new ProjectCodebaseStatusDto(normalized, 0, 0, null, dbPath));
            }
        }

        return Ok(result);
    }

    /// <summary>
    /// Deletes a project from the registry, with options to delete its codebase index
    /// and/or its local .eling data folder.
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

        // 1. Delete from workspaces database and unregister if active
        await registry.DeleteWorkspaceAsync(normalized, cancellationToken);

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
}