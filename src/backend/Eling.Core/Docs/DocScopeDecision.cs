namespace Eling.Core.Docs;

using Eling.Core.Scope;

/// <summary>
/// Minimal scope decision for index placement (reuses memory's decision enum).
/// </summary>
public sealed class DocScopeDecision
{
    public ScopeDecision Decision { get; set; } = ScopeDecision.Ask;
}