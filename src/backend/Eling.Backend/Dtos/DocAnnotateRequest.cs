namespace Eling.Backend.Dtos;

/// <summary>
/// Request to annotate a document section.
/// </summary>
public sealed record DocAnnotateRequest(
    string Anchor,
    int Status,
    string? Annotation = null);