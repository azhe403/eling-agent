using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Eling.Desktop.Models;

namespace Eling.Desktop.Services;

public static class WorkspaceAggregator
{
    public sealed record AggregateResult(
        List<WorkspaceItemDto> Workspaces,
        int RegisteredCount,
        int ActiveInstancesCount,
        int ActiveWorkspacesCount,
        int TotalFilesCount,
        long TotalDbSizeBytes);

    public static AggregateResult Aggregate(
        IReadOnlyList<CoordinatorRuntimeDto> runtimes,
        IReadOnlyList<ProjectCodebaseStatusDto> statuses,
        IReadOnlyList<string>? agentRoots)
    {
        var statusMap = new Dictionary<string, ProjectCodebaseStatusDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in statuses)
        {
            if (!string.IsNullOrWhiteSpace(s.WorkspaceRoot))
            {
                statusMap[s.WorkspaceRoot.TrimEnd('\\', '/')] = s;
            }
        }

        var groups = new Dictionary<string, List<CoordinatorRuntimeDto>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in runtimes)
        {
            var root = !string.IsNullOrWhiteSpace(r.WorkspaceRoot) ? r.WorkspaceRoot : r.HeadScopeRoot;
            if (string.IsNullOrWhiteSpace(root) || string.Equals(root, "UserScope", StringComparison.OrdinalIgnoreCase))
                continue;

            var norm = root.TrimEnd('\\', '/');
            if (!groups.TryGetValue(norm, out var list))
            {
                list = [];
                groups[norm] = list;
            }
            list.Add(r);
        }

        if (agentRoots != null)
        {
            foreach (var root in agentRoots)
            {
                var norm = root.TrimEnd('\\', '/');
                if (!groups.ContainsKey(norm))
                {
                    groups[norm] = [];
                }
            }
        }

        var workspaces = new List<WorkspaceItemDto>();
        var totalFiles = 0;
        long totalDbBytes = 0;
        var activeInstances = 0;
        var activeWorkspaces = 0;

        foreach (var groupEntry in groups)
        {
            var root = groupEntry.Key;
            var rows = groupEntry.Value;
            var name = Path.GetFileName(root);
            if (string.IsNullOrEmpty(name)) name = root;

            var liveRows = rows.Where(r => r.IsAlive && r.ProcessId > 0).ToList();
            var isRunning = liveRows.Count > 0;
            if (isRunning)
            {
                activeWorkspaces++;
                activeInstances += liveRows.Count;
            }

            var procItems = liveRows
                .OrderBy(r => r.StartTime)
                .Select(r => new WorkspaceProcessItem(
                    r.ProcessId,
                    r.StartTime,
                    r.McpTransport,
                    r.IsDashboardOwner,
                    r.DashboardPort,
                    r.MemoryBytes,
                    r.CpuPercent))
                .ToList();

            var singlePid = liveRows.Count == 1 ? liveRows[0].ProcessId : (int?)null;
            var transport = rows.FirstOrDefault(r => !string.IsNullOrEmpty(r.McpTransport))?.McpTransport;
            var ownerPort = liveRows.FirstOrDefault(r => r.IsDashboardOwner)?.DashboardPort;
            var lastHeartbeat = rows.Count > 0 ? rows.Max(r => r.LastHeartbeat) : default;

            var memValues = liveRows.Where(r => r.MemoryBytes.HasValue).Select(r => r.MemoryBytes!.Value).ToList();
            var cpuValues = liveRows.Where(r => r.CpuPercent.HasValue).Select(r => r.CpuPercent!.Value).ToList();
            long? totalMem = memValues.Count > 0 ? memValues.Sum() : null;
            double? totalCpu = cpuValues.Count > 0 ? cpuValues.Sum() : null;

            statusMap.TryGetValue(root, out var codeStatus);
            var files = codeStatus?.Files ?? 0;
            var chunks = codeStatus?.Chunks ?? 0;
            var lastIndexed = codeStatus?.LastIndexedAt;
            var dbPath = codeStatus?.DbPath;
            var dbSize = codeStatus?.DbSizeBytes ?? 0;
            var dbFileName = !string.IsNullOrEmpty(dbPath) ? Path.GetFileName(dbPath) : null;

            totalFiles += files;
            totalDbBytes += dbSize;

            var item = new WorkspaceItemDto(
                root,
                name,
                isRunning,
                liveRows.Count,
                singlePid,
                transport,
                ownerPort,
                lastHeartbeat,
                totalMem,
                totalCpu,
                files,
                chunks,
                lastIndexed,
                dbFileName,
                dbPath,
                dbSize,
                procItems);

            workspaces.Add(item);
        }

        return new AggregateResult(
            workspaces,
            workspaces.Count,
            activeInstances,
            activeWorkspaces,
            totalFiles,
            totalDbBytes);
    }
}
