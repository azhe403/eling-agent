namespace Eling.Core.Docs;

/// <summary>
/// A section within a document, corresponding to a heading and its content range.
/// </summary>
public sealed class DocSection
{
    public DocId DocId { get; }
    public string Anchor { get; }
    public string Heading { get; }
    public int Level { get; }
    public int LineStart { get; }
    public int LineEnd { get; }
    public DocStatusValue Status { get; set; }
    public string? Annotation { get; set; }

    public DocSection(
        DocId docId,
        string anchor,
        string heading,
        int level,
        int lineStart,
        int lineEnd,
        DocStatusValue status,
        string? annotation = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(anchor);
        ArgumentException.ThrowIfNullOrWhiteSpace(heading);

        DocId = docId;
        Anchor = anchor;
        Heading = heading;
        Level = level;
        LineStart = lineStart;
        LineEnd = lineEnd;
        Status = status;
        Annotation = annotation;
    }

    public bool HasAnnotation => !string.IsNullOrWhiteSpace(Annotation);
}