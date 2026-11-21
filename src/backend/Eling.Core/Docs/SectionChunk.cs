namespace Eling.Core.Docs;

/// <summary>Result of chunking a section.</summary>
public sealed class SectionChunk
{
    public string Anchor { get; set; } = "";
    public string Heading { get; set; } = "";
    public int Level { get; set; }
    public int LineStart { get; set; }
    public int LineEnd { get; set; }
    public string RawHeadingText { get; set; } = "";
}