namespace Eling.Core.Docs;

/// <summary>
/// Result of parsing front-matter.
/// </summary>
public sealed record ParseResult(
    DocFrontMatter? FrontMatter,
    string RemainingContent,
    string? ErrorMessage);