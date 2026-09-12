using System.ComponentModel;
using Eling.Backend.Dtos;
using Eling.Backend.Scope;
using Eling.Core.Scope;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Tools;

/// <summary>
/// Records a machine-local project-scope policy decision: disable project scope
/// for this workspace / a path glob / by default, re-enable it, or clear an
/// override. Purely a user-driven action — the agent must only call it when the
/// user explicitly asks; it is never invoked automatically. Async, matching the
/// async policy store.
/// </summary>
[McpServerToolType]
public sealed class MemoryProjectScopePolicyTool
{
    private readonly IProjectScopePolicyStore _store;
    private readonly string _cwd;
    private readonly string _userHome;
    private readonly ILogger<MemoryProjectScopePolicyTool>? _logger;

    public MemoryProjectScopePolicyTool(
        IProjectScopePolicyStore store,
        string? cwd = null,
        string? userHomeDirectory = null,
        ILogger<MemoryProjectScopePolicyTool>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _cwd = string.IsNullOrWhiteSpace(cwd) ? Directory.GetCurrentDirectory() : Path.GetFullPath(cwd);
        _userHome = string.IsNullOrWhiteSpace(userHomeDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : Path.GetFullPath(userHomeDirectory);
        _logger = logger;
    }

    [McpServerTool(Name = "memory_project_scope_policy"), Description("Set a machine-local project-scope policy decision. decision: 'ask' (offer consent-gated onboarding), 'disabled' (project scope off; writes go to global), or 'clear' (remove an override). target: 'project' (this workspace), 'pattern' (a path glob), or 'default'. Only call this when the user explicitly asks to disable or re-enable project scope.")]
    public async Task<MemoryProjectScopePolicyResultDto> SetAsync(
        [Description("ask | disabled | clear")] string decision,
        [Description("project (this workspace) | pattern | default")] string target = "project",
        [Description("Glob path rule; required when target=pattern, e.g. ~/work/acme/legacy/**")] string? pattern = null)
    {
        var normalizedDecision = decision?.Trim().ToLowerInvariant();
        if (normalizedDecision is not ("ask" or "disabled" or "clear"))
        {
            return Error(
                "project", "invalid_argument",
                $"decision must be ask, disabled, or clear (got '{decision}').");
        }

        var normalizedTarget = string.IsNullOrWhiteSpace(target) ? "project" : target.Trim().ToLowerInvariant();

        try
        {
            ProjectScopePolicy policy;
            string? appliedTo;

            switch (normalizedTarget)
            {
                case "project":
                    if (IsUserHome(_cwd, _userHome))
                    {
                        return Error(
                            normalizedTarget, "rejected_user_home",
                            "Project scope policy cannot target the user home.");
                    }

                    policy = normalizedDecision == "clear"
                        ? await _store.ClearProjectAsync(_cwd)
                        : await _store.SetProjectAsync(_cwd, Parse(normalizedDecision));
                    appliedTo = ProjectScopePolicy.NormalizeRoot(_cwd);
                    break;

                case "pattern":
                    if (string.IsNullOrWhiteSpace(pattern))
                    {
                        return Error(
                            normalizedTarget, "invalid_argument",
                            "pattern is required when target=pattern.");
                    }

                    policy = normalizedDecision == "clear"
                        ? await _store.ClearPatternAsync(pattern)
                        : await _store.SetPatternAsync(pattern, Parse(normalizedDecision));
                    appliedTo = pattern;
                    break;

                case "default":
                    if (normalizedDecision == "clear")
                    {
                        return Error(
                            normalizedTarget, "invalid_argument",
                            "clear is not supported for target=default; set 'ask' or 'disabled'.");
                    }

                    policy = await _store.SetDefaultAsync(Parse(normalizedDecision));
                    appliedTo = "default";
                    break;

                default:
                    return Error(
                        normalizedTarget, "invalid_argument",
                        $"target must be project, pattern, or default (got '{target}').");
            }

            _logger?.LogInformation(
                "memory_project_scope_policy applied {Decision} to {Target} ({AppliedTo})",
                normalizedDecision, normalizedTarget, appliedTo);

            return new MemoryProjectScopePolicyResultDto
            {
                Ok = true,
                Target = normalizedTarget,
                Decision = normalizedDecision,
                AppliedTo = appliedTo,
                Policy = ProjectScopePolicyDto.From(policy)
            };
        }
        catch (KeyNotFoundException ex)
        {
            return Error(normalizedTarget, "not_found", ex.Message);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "memory_project_scope_policy failed for {Target}", normalizedTarget);
            return Error(normalizedTarget, "internal_error", ex.Message);
        }
    }

    private static ProjectScopeDecision Parse(string decision)
        => decision == "disabled" ? ProjectScopeDecision.Disabled : ProjectScopeDecision.Ask;

    private static bool IsUserHome(string cwd, string home)
        => string.Equals(
            cwd.TrimEnd(Path.DirectorySeparatorChar),
            home.TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static MemoryProjectScopePolicyResultDto Error(string target, string code, string message) => new()
    {
        Ok = false,
        Target = target,
        Error = message,
        Code = code
    };
}
