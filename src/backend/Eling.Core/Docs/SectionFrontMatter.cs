namespace Eling.Core.Docs;

/// <summary>
/// Per-section front-matter for status and annotation.
/// </summary>
public sealed class SectionFrontMatter
{
    public int? Status { get; set; }
    public string? Annotation { get; set; }
}