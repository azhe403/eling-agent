namespace Eling.Core.Database;

/// <summary>
/// The cluster could not be reached, answered too late (timeout), or answered with
/// something the client cannot read. Distinct from <see cref="ElasticsearchApiException"/>
/// so the HTTP layer can map it to 503 (temporarily unavailable) rather than a
/// 5xx dressed as a generic 500.
/// </summary>
public sealed class ElasticsearchUnavailableException : Exception
{
    public ElasticsearchUnavailableException(string reason)
        : base(reason)
    {
    }
}
