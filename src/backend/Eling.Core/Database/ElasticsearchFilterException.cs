namespace Eling.Core.Database;

/// <summary>
/// Thrown when a filter row decoded from a query string cannot be compiled
/// into the Elasticsearch DSL. The message is safe to return directly as a
/// 400 response body: it carries only the offending field/op name and the
/// constraint that was violated - never a host name, user name, index name
/// or cluster detail.
/// </summary>
public sealed class ElasticsearchFilterException : Exception
{
    public ElasticsearchFilterException(string message)
        : base(message)
    {
    }
}
