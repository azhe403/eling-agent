namespace Eling.Core.Docs;

/// <summary>
/// Raised when a write targets an external document (a file under docs/ owned by
/// its source tree). Callers map this to HTTP 409 doc_read_only rather than
/// matching on message text.
/// </summary>
public sealed class DocReadOnlyException : InvalidOperationException
{
    public DocReadOnlyException(string message) : base(message)
    {
    }
}
