using System.ComponentModel;
using Eling.Backend.Dtos;
using Eling.Core;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Tools;

/// <summary>
/// Reports the current project-scope posture for the working directory so a
/// client can decide whether user consent for project initialization is
/// needed. Pure read — never creates anything on disk.
/// </summary>
[McpServerToolType]
public sealed class MemoryProjectStatusTool
{
    private readonly string _cwd;
    private readonly string? _userHomeDirectory;

    public MemoryProjectStatusTool(string? cwd = null, string? userHomeDirectory = null)
    {
        _cwd = string.IsNullOrWhiteSpace(cwd) ? Directory.GetCurrentDirectory() : Path.GetFullPath(cwd);
        _userHomeDirectory = string.IsNullOrWhiteSpace(userHomeDirectory)
            ? null
            : Path.GetFullPath(userHomeDirectory);
    }

    [McpServerTool(Name = "memory_project_status"), Description("Report the project-scope posture of the current working directory: own-scope, ancestor-scope, uninitialized, or user-home, plus whether this project can be adopted (needs memory_init_project consent).")]
    public Task<ProjectStatusDto> GetStatusAsync()
    {
        var chain = ScopeChain.Discover(_cwd);
        var userHome = _userHomeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var isUserHome = !string.IsNullOrWhiteSpace(userHome) &&
            string.Equals(
                _cwd.TrimEnd(Path.DirectorySeparatorChar),
                userHome.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);

        var posture = isUserHome ? "user-home" :
            chain.HasOwnScope ? "own-scope" :
            chain.IsInitialized ? "ancestor-scope" :
            "uninitialized";

        var dto = new ProjectStatusDto(
            Cwd: _cwd,
            IsUserHome: isUserHome,
            Initialized: chain.IsInitialized,
            HasOwnScope: chain.HasOwnScope,
            HeadRoot: chain.Head?.Root,
            Adoptable: !isUserHome && !chain.HasOwnScope,
            AncestorScopes: chain.Levels.Skip(chain.HasOwnScope ? 1 : 0).Select(l => l.Root).ToList().AsReadOnly(),
            Posture: posture);

        return Task.FromResult(dto);
    }
}