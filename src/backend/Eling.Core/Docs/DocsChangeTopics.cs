namespace Eling.Core.Docs;

/// <summary>
/// The docs-catalog change topic on the shared change stream
/// (/api/events/docs). Single constant so the owner's in-proc adapter,
/// the MCP host's HTTP relay, the catalog watcher and the frontend all agree
/// on the wire value.
/// </summary>
public static class DocsChangeTopics
{
    public const string Docs = "docs";
}