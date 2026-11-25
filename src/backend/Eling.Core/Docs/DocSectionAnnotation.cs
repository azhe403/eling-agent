namespace Eling.Core.Docs;

/// <summary>A section annotation stored in committed metadata.</summary>
public sealed class DocSectionAnnotation
{
    public int Status { get; set; }
    public string? Text { get; set; }
}
