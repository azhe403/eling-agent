using Eling.Backend.Dtos;
using Eling.Backend.Scope;
using Eling.Core.Scope;

namespace Eling.Backend.Bootstrap;

/// <summary>
/// Shared evaluation of a workspace's project-scope posture: the scope-chain
/// facts plus the resolved policy decision. Used by <c>memory_recall</c> and
/// <c>memory_project_status</c> so both agree, and so onboarding is offered only
/// when the policy is <c>ask</c>. Read-only: never creates <c>.eling</c>.
/// </summary>
public static class ProjectScopePosture
{
    public static async Task<ProjectStatusDto> EvaluateAsync(
        string? cwd = null,
        string? userHomeDirectory = null,
        IProjectScopePolicyStore? policyStore = null)
    {
        var normalizedCwd = string.IsNullOrWhiteSpace(cwd)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(cwd);
        var chain = ScopeChain.Discover(normalizedCwd);

        var home = string.IsNullOrWhiteSpace(userHomeDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : Path.GetFullPath(userHomeDirectory);
        var isUserHome = !string.IsNullOrWhiteSpace(home) &&
            string.Equals(
                normalizedCwd.TrimEnd(Path.DirectorySeparatorChar),
                home.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);

        var policy = isUserHome || policyStore is null
            ? ProjectScopePolicy.DefaultPolicy
            : await policyStore.LoadAsync();
        var resolution = policy.ResolveDetailed(normalizedCwd);

        var posture = isUserHome ? "user-home" :
            chain.HasOwnScope ? "own-scope" :
            chain.IsInitialized ? "ancestor-scope" :
            "uninitialized";

        return new ProjectStatusDto(
            Cwd: normalizedCwd,
            IsUserHome: isUserHome,
            Initialized: chain.IsInitialized,
            HasOwnScope: chain.HasOwnScope,
            HeadRoot: chain.Head?.Root,
            Adoptable: !isUserHome && !chain.HasOwnScope && resolution.Decision == ProjectScopeDecision.Ask,
            AncestorScopes: chain.Levels.Skip(chain.HasOwnScope ? 1 : 0).Select(level => level.Root).ToList().AsReadOnly(),
            Posture: posture,
            Policy: resolution.Decision == ProjectScopeDecision.Disabled ? "disabled" : "ask",
            PolicyLevel: resolution.MatchedLevel.ToString().ToLowerInvariant(),
            PolicyEntryCreatedAt: resolution.Entry?.CreatedAt,
            PolicyEntryUpdatedAt: resolution.Entry?.UpdatedAt,
            PolicyDocumentCreatedAt: policy.CreatedAt,
            PolicyDocumentUpdatedAt: policy.UpdatedAt);
    }
}
