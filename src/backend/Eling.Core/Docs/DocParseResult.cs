namespace Eling.Core.Docs;

/// <summary>
/// Result of parsing a document into Doc, sections, references, and relations.
/// </summary>
public sealed record DocParseResult(
    Doc? Doc,
    IReadOnlyList<DocSection> Sections,
    IReadOnlyList<DocRef> Refs,
    IReadOnlyList<DocRelation> Relations,
    string? Body = null);

/// <summary>
/// Result of heading chunking.
/// </summary>
public sealed record ChunkResult(
    IReadOnlyList<SectionChunk> Sections,
    string Preamble);

/// <summary>
/// Result of counting checkboxes.
/// </summary>
public sealed record CheckboxCounts(
    int Total,
    int Done);

/// <summary>
/// Per-section checkbox counts.
/// </summary>
public sealed record SectionCheckboxCounts(
    string Anchor,
    int Total,
    int Done);