namespace Eling.Core.Docs.Storage;

/// <summary>
/// Section row id and heading for FTS operations.
/// </summary>
public sealed record SectionRowIdHeading(
    long RowId,
    string Heading);