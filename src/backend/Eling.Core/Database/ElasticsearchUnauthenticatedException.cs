namespace Eling.Core.Database;

/// <summary>
/// The cluster refused the credentials (ES 401/403). Distinct from
/// <see cref="ElasticsearchApiException"/> because the fix is a specific one —
/// check username and password — not "the cluster is broken".
/// </summary>
public sealed class ElasticsearchUnauthenticatedException : Exception
{
    public ElasticsearchUnauthenticatedException(string reason)
        : base(reason)
    {
    }
}
