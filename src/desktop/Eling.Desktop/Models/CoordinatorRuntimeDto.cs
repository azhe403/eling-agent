using System;

namespace Eling.Desktop.Models;

public sealed record CoordinatorRuntimeDto(
    int ProcessId,
    string HeadScopeRoot,
    string WorkspaceRoot,
    bool CodebaseEnabled,
    string DataDirectory,
    DateTimeOffset StartTime,
    bool McpEnabled,
    string McpTransport,
    DateTimeOffset LastHeartbeat,
    bool IsAlive,
    long? MemoryBytes = null,
    double? CpuPercent = null,
    bool IsDashboardOwner = false,
    int? DashboardPort = null,
    string? RepositoryRoot = null);
