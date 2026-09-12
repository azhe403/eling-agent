namespace Eling.Core.Scope;

/// <summary>
/// A stored project-scope policy decision.
/// </summary>
public enum ProjectScopeDecision
{
    /// <summary>Offer consent-gated onboarding when the workspace is eligible.</summary>
    Ask,

    /// <summary>
    /// Project scope is off for the workspace: onboarding is never offered and
    /// project-targeted operations resolve to global.
    /// </summary>
    Disabled
}
