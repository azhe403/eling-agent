namespace Eling.Core.Docs;

/// <summary>
/// Result of reading a document with optional sections and references.
/// </summary>
public sealed class DocReadResult
{
    public DocReadResult(
        Doc document,
        IReadOnlyList<DocSection>? sections = null,
        IReadOnlyList<DocRef>? references = null,
        string? content = null)
    {
        Document = document;
        Sections = sections;
        References = references;
        Content = content;
    }

    public Doc Document { get; }
    public IReadOnlyList<DocSection>? Sections { get; }
    public IReadOnlyList<DocRef>? References { get; }

    /// <summary>
    /// The document's raw markdown, when the caller asked for it. Null means the
    /// content was not requested or the file could not be located on disk.
    /// </summary>
    public string? Content { get; }
}