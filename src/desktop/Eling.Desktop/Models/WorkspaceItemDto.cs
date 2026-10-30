using System;
using System.Collections.Generic;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace Eling.Desktop.Models;

public sealed class WorkspaceItemDto : ReactiveObject
{
    private bool _isExpanded;

    public string Root { get; }
    public string? RepositoryRoot { get; }
    public string Name { get; }
    public bool IsRunning { get; }
    public int ProcessCount { get; }
    public int? SinglePid { get; }
    public string? Transport { get; }
    public int? OwnerPort { get; }
    public DateTimeOffset LastHeartbeat { get; }
    public long? TotalMemoryBytes { get; }
    public double? TotalCpuPercent { get; }
    public int Files { get; }
    public int Chunks { get; }
    public string? LastIndexedAt { get; }
    public string? DbFileName { get; }
    public string? DbPath { get; }
    public long DbSizeBytes { get; }
    public IReadOnlyList<WorkspaceProcessItem> Processes { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => this.RaiseAndSetIfChanged(ref _isExpanded, value);
    }

    public bool HasMultipleProcesses => IsRunning && Processes.Count > 1;

    public WorkspaceItemDto(
        string root,
        string? repositoryRoot,
        string name,
        bool isRunning,
        int processCount,
        int? singlePid,
        string? transport,
        int? ownerPort,
        DateTimeOffset lastHeartbeat,
        long? totalMemoryBytes,
        double? totalCpuPercent,
        int files,
        int chunks,
        string? lastIndexedAt,
        string? dbFileName,
        string? dbPath,
        long dbSizeBytes,
        IReadOnlyList<WorkspaceProcessItem> processes)
    {
        Root = root;
        RepositoryRoot = repositoryRoot;
        Name = name;
        IsRunning = isRunning;
        ProcessCount = processCount;
        SinglePid = singlePid;
        Transport = transport;
        OwnerPort = ownerPort;
        LastHeartbeat = lastHeartbeat;
        TotalMemoryBytes = totalMemoryBytes;
        TotalCpuPercent = totalCpuPercent;
        Files = files;
        Chunks = chunks;
        LastIndexedAt = lastIndexedAt;
        DbFileName = dbFileName;
        DbPath = dbPath;
        DbSizeBytes = dbSizeBytes;
        Processes = processes;
    }

    public string StatusBadgeText => IsRunning
        ? (ProcessCount == 1 ? $"Running (PID: {SinglePid})" : $"Running · {ProcessCount} instances")
        : "Offline";

    public string StatusBadgeBg => IsRunning ? "#143a25" : "#262626";
    public string StatusBadgeFg => IsRunning ? "#4ade80" : "#9ca3af";
    public string DotColor => IsRunning ? "#22c55e" : "#6b7280";

    public string MemoryDisplay => TotalMemoryBytes.HasValue
        ? $"{TotalMemoryBytes.Value / 1024.0 / 1024.0:F1} MB"
        : "—";

    public string CpuDisplay => TotalCpuPercent.HasValue
        ? $"{TotalCpuPercent.Value:F1}%"
        : "—";

    public string DbSizeDisplay => DbSizeBytes > 0
        ? FormatBytes(DbSizeBytes)
        : "—";

    public string FilesDisplay => Files > 0 || Chunks > 0
        ? $"{Files:N0} files ({Chunks:N0} chunks)"
        : "0 files";

    public string LastActiveDisplay => LastHeartbeat != default
        ? LastHeartbeat.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
        : "—";

    public string IndexedDisplay => !string.IsNullOrEmpty(LastIndexedAt) && DateTimeOffset.TryParse(LastIndexedAt, out var dt)
        ? dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
        : "Never";

    public bool HasDbInfo => !string.IsNullOrEmpty(DbFileName) && (DbSizeBytes > 0 || !string.IsNullOrEmpty(LastIndexedAt));

    public string ChevronGlyph => IsExpanded ? "▴" : "▾";

    public void ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
        this.RaisePropertyChanged(nameof(ChevronGlyph));
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:F1} MB";
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:F1} GB";
    }
}