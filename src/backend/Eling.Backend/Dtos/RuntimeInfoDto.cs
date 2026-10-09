using Eling.Core.Runtime;

namespace Eling.Backend.Dtos;

/// <summary>
/// A registered runtime as the dashboard consumes it. Mirrors
/// <see cref="RuntimeInfo"/>'s JSON shape so existing clients are unaffected by
/// this DTO's existence.
/// </summary>
/// <param name="ProcessId">
/// The owning process, or 0 for a workspace that has registered but whose
/// process is gone. 0 is the marker that distinguishes a remembered project
/// from a running one.
/// </param>
/// <param name="HeadScopeRoot">
/// The nearest <c>.eling</c> at or above the workspace — the head of the scope
/// chain, where memories are written. Not a project root, and the
/// <c>UserScope</c> sentinel for a user-home session.
/// </param>
/// <param name="WorkspaceRoot">The working directory the backend ran in — the codebase index root.</param>
/// <param name="CodebaseEnabled">False when the workspace opted out of codebase indexing.</param>
/// <param name="DataDirectory">The workspace's .eling directory.</param>
/// <param name="StartTime">When the workspace was first recorded.</param>
/// <param name="McpEnabled">Whether the runtime exposed MCP.</param>
/// <param name="McpTransport">The MCP transport it used.</param>
/// <param name="LastHeartbeat">Last seen live, or last registered when not running.</param>
/// <param name="IsAlive">False for a workspace that is only remembered.</param>
/// <param name="MemoryBytes">Working set memory in bytes for live process.</param>
/// <param name="CpuPercent">CPU usage percent for live process.</param>
public sealed record RuntimeInfoDto(
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
    double? CpuPercent = null)
{
    public static RuntimeInfoDto From(RuntimeInfo runtime) => new(
        runtime.ProcessId,
        runtime.HeadScopeRoot,
        runtime.WorkspaceRoot,
        runtime.CodebaseEnabled,
        runtime.DataDirectory,
        runtime.StartTime,
        runtime.McpEnabled,
        runtime.McpTransport,
        runtime.LastHeartbeat,
        runtime.IsAlive);
}
