namespace Eling.Core.Docs;

/// <summary>
/// Front-matter structure parsed from Markdown documents.
/// </summary>
public sealed class DocFrontMatter
{
    public DocKind? Kind { get; set; }
    public string? Topic { get; set; }
    public int? Order { get; set; }
    public int? Status { get; set; }
    public string? SupersededBy { get; set; }
    public string? Spec { get; set; }
    public Dictionary<string, SectionFrontMatter>? Sections { get; set; }
}