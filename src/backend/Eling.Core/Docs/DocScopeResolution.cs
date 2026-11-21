namespace Eling.Core.Docs;

using Eling.Core.Scope;

/// <summary>
/// Result of resolving a document path to its scope properties.
/// </summary>
public sealed record DocScopeResolution(
    DocOwnership Ownership,
    DocOrigin Origin,
    string RelativePath,
    string RootPath);