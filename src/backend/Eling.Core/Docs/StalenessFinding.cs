namespace Eling.Core.Docs;

/// <summary>
/// Staleness finding.
/// </summary>
public sealed record StalenessFinding(
    string DocId,
    string Title,
    string RelPath,
    StalenessKind Kind,
    string Message,
    string? Anchor);

/// <summary>
/// Staleness report.
/// </summary>
public sealed record StalenessReport(
    IReadOnlyList<StalenessFinding> Findings,
    DateTimeOffset GeneratedAt);