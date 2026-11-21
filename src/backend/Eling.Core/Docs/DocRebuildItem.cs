namespace Eling.Core.Docs;

/// <summary>
/// Rebuild item for index rebuilding.
/// </summary>
public sealed record DocRebuildItem(
    Doc Doc,
    IReadOnlyList<DocSection> Sections,
    IReadOnlyList<DocRef> Refs);