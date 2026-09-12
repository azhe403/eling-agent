namespace Eling.Core.Scope;

/// <summary>
/// The outcome of resolving a project root against a policy: the decision plus
/// which level produced it and the matching entry (null when the decision came
/// from <c>default</c>).
/// </summary>
public sealed record ProjectScopeResolution(
    ProjectScopeDecision Decision,
    ProjectScopeResolution.Level MatchedLevel,
    ProjectScopeEntry? Entry)
{
    /// <summary>Which policy level produced the decision.</summary>
    public enum Level
    {
        Default,
        Pattern,
        Project
    }
}
