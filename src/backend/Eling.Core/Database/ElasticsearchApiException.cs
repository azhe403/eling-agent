namespace Eling.Core.Database;

/// <summary>
/// An Elasticsearch call answered with an HTTP error status (4xx/5xx) the client
/// surfaced as a typed response. Carries the raw status so the caller can map it:
/// 401/403 becomes an authentication failure, 404 stays 404 for a missing index,
/// anything else is an API error with the cluster's own reason text.
/// </summary>
public sealed class ElasticsearchApiException : Exception
{
    /// <summary>The HTTP status the cluster answered with (e.g. 400, 404, 500).</summary>
    public int Status { get; }

    /// <summary>The cluster's own reason text, safe to show on a 4xx to the caller.</summary>
    public string Reason { get; }

    public ElasticsearchApiException(int status, string reason)
        : base(reason)
    {
        Status = status;
        Reason = reason;
    }
}
