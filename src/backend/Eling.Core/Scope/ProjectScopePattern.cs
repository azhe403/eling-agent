namespace Eling.Core.Scope;

/// <summary>
/// A glob path rule mapped to a decision and its dates.
/// </summary>
public sealed record ProjectScopePattern(
    string Glob,
    ProjectScopeEntry Entry);
