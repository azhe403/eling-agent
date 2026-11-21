namespace Eling.Core.Docs;

/// <summary>
/// A backtick-quoted reference found in a document section, with its resolution state.
/// </summary>
public sealed class DocRef
{
    public DocId DocId { get; }
    public string Anchor { get; }
    public string RawText { get; }
    public ReferenceKind RefKind { get; init; }
    public string? ResolvedPath { get; set; }
    public int? ResolvedLine { get; set; }
    public bool Resolved { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }

    public DocRef(
        DocId docId,
        string anchor,
        string rawText,
        ReferenceKind refKind = ReferenceKind.Symbol,
        string? resolvedPath = null,
        int? resolvedLine = null,
        bool resolved = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(anchor);
        ArgumentException.ThrowIfNullOrWhiteSpace(rawText);

        DocId = docId;
        Anchor = anchor;
        RawText = rawText;
        RefKind = refKind;
        ResolvedPath = resolvedPath;
        ResolvedLine = resolvedLine;
        Resolved = resolved;
    }
}

/// <summary>
/// Classification of a backtick reference.
/// </summary>
public enum ReferenceKind
{
    Symbol,
    Path,
    AnchorLink
}