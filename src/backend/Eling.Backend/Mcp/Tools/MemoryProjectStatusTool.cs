using System.ComponentModel;
using Eling.Backend.Bootstrap;
using Eling.Backend.Dtos;
using Eling.Backend.Scope;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Tools;

/// <summary>
/// Reports the current project-scope posture for the working directory so a
/// client can decide whether user consent for project initialization is
/// needed. Pure read — never creates anything on disk. Now policy-aware: a
/// workspace disabled by the machine-local policy is never adoptable.
/// </summary>
[McpServerToolType]
public sealed class MemoryProjectStatusTool
{
    private readonly string _cwd;
    private readonly string? _userHomeDirectory;
    private readonly IProjectScopePolicyStore? _policyStore;

    public MemoryProjectStatusTool(
        string? cwd = null,
        string? userHomeDirectory = null,
        IProjectScopePolicyStore? policyStore = null)
    {
        _cwd = string.IsNullOrWhiteSpace(cwd) ? Directory.GetCurrentDirectory() : Path.GetFullPath(cwd);
        _userHomeDirectory = string.IsNullOrWhiteSpace(userHomeDirectory)
            ? null
            : Path.GetFullPath(userHomeDirectory);
        _policyStore = policyStore;
    }

    [McpServerTool(Name = "memory_project_status"), Description("Report the project-scope posture of the current working directory: own-scope, ancestor-scope, uninitialized, or user-home, the resolved project-scope policy (ask | disabled), whether this project can be adopted (needs memory_init_project consent), and the policy decision's dates.")]
    public Task<ProjectStatusDto> GetStatusAsync()
        => ProjectScopePosture.EvaluateAsync(_cwd, _userHomeDirectory, _policyStore);
}
