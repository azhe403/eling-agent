namespace Eling.Mcp;

/// <summary>
/// Static text delivered to MCP clients during the initialize handshake.
/// </summary>
public static class ServerInstructions
{
    public const string Default =
        "Eling is a durable markdown-backed memory system. In Eling, memory Markdown files under '.eling/memories/' are the canonical source of truth and MUST be tracked in Git. " +
        "Generated runtime files (such as '.eling/logs/', '.eling/index.db*', '.eling/*.db', '.eling/*.db-journal', '.eling/*.db-wal', '.eling/runtime/') MUST be added to '.gitignore'. " +
        "When running in a project workspace, always check if the project's '.gitignore' properly ignores these Eling runtime files while keeping '.eling/memories/' tracked. " +
        "If '.gitignore' is missing or does not include these runtime ignore patterns, prompt the user for confirmation to update '.gitignore' with standard Eling ignore patterns.";
}
