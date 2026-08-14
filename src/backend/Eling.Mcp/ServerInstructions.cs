namespace Eling.Mcp;

/// <summary>
/// Static text delivered to MCP clients during the initialize handshake.
/// </summary>
public static class ServerInstructions
{
    public const string Default = "Eling is a durable markdown-backed memory system. In Eling, memory Markdown files under '.eling/memories/' are the canonical source of truth and are intended to be tracked in Git. Generated runtime files (such as '.eling/index.db' SQLite indexes and '.eling/logs/') may be added to '.gitignore'. Do not ignore the '.eling/memories/' directory so that canonical memories remain version-controlled.";
}
