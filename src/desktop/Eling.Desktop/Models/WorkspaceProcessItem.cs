using System;

namespace Eling.Desktop.Models;

public sealed record WorkspaceProcessItem(
    int Pid,
    DateTimeOffset StartTime,
    string Transport,
    bool IsOwner,
    int? OwnerPort,
    long? MemoryBytes,
    double? CpuPercent)
{
    public string OwnerBadgeText => IsOwner ? "owner" : "peer";
    public string OwnerBadgeBg => IsOwner ? "#1e293b" : "#222226";
    public string OwnerBadgeFg => IsOwner ? "#60a5fa" : "#9ca3af";

    public string MetricsDisplay
    {
        get
        {
            if (MemoryBytes.HasValue && CpuPercent.HasValue)
                return $"{MemoryBytes.Value / 1024.0 / 1024.0:F1} MB · {CpuPercent.Value:F1}%";
            if (MemoryBytes.HasValue)
                return $"{MemoryBytes.Value / 1024.0 / 1024.0:F1} MB";
            if (CpuPercent.HasValue)
                return $"{CpuPercent.Value:F1}%";
            return "—";
        }
    }
}
