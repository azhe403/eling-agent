namespace Eling.Core.Docs;

/// <summary>Result of loading workspace metadata, with non-fatal entry findings.</summary>
public sealed record MetadataLoadResult(
    DocsMetadataDocument Document,
    IReadOnlyList<string> Findings);
