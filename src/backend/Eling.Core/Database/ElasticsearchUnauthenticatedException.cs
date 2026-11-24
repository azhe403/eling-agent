namespace Eling.Core.Database;

/// <summary>
/// The cluster refused the request (ES 401: missing credentials, ES 403: the
/// authenticated account lacks a privilege). Distinct from
/// <see cref="ElasticsearchApiException"/> because the fix is specific to the
/// account — credentials or privileges — not "the cluster is broken".
/// </summary>
public sealed class ElasticsearchUnauthenticatedException : Exception
{
    public ElasticsearchUnauthenticatedException(int status, string reason)
        : base(reason)
    {
        Status = status;
    }

    public ElasticsearchUnauthenticatedException(string reason)
        : this(401, reason)
    {
    }

    /// <summary>The ES status that caused the refusal: 401 or 403.</summary>
    public int Status { get; }

    /// <summary>
    /// Caller-safe explanation. The server reason can name the account, so it
    /// is never included here — only the status and the matching remediation.
    /// </summary>
    public string CallerSafeDetail => Status == 403
        ? "authentication failed (403): the authenticated account lacks permission for this request"
        : "authentication failed (401): the cluster requires credentials";
}
