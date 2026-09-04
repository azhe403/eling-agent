namespace Eling.Backend.Mcp;

/// <summary>
/// Static text delivered to MCP clients during the initialize handshake.
/// </summary>
public static class ServerInstructions
{
    /// <summary>
    /// Individual instruction sections broken down by semantic responsibility.
    /// </summary>
    public static readonly string[] Sections =
    [
        "Eling is a durable markdown-backed memory system. In Eling, memory Markdown files under '.eling/memories/' are the canonical source of truth and MUST be tracked in Git.",

        "Generated runtime files (such as '.eling/logs/', '.eling/index.db*', '.eling/*.db', '.eling/*.db-journal', '.eling/*.db-wal', '.eling/runtime/') MUST be added to '.gitignore'.",

        "When running in a project workspace, always check if the project's '.gitignore' properly ignores these Eling runtime files while keeping '.eling/memories/' tracked.",

        "If '.gitignore' is missing or does not include these runtime ignore patterns, prompt the user for confirmation to update '.gitignore' with standard Eling ignore patterns.",

        "Memory Recall Strategy: Hydrate context at session start, upon major topic shifts, or before non-trivial planning. Avoid redundant recall on every micro-turn to preserve latency and context window. On explicit user recall requests ('eling <something>'), retrieve the relevant memories immediately.",

        "Usage: 'Eling <something>' (Javanese for 'remember <something>') is a recall instruction — " +
        "it means retrieving a memory previously stored in Eling's memory management " +
        "(e.g. 'eling cara build' = recall the build steps saved in Eling memories)."
    ];

    /// <summary>
    /// Merged instruction text joined by double newlines for clear paragraph separation.
    /// </summary>
    public static readonly string Default = string.Join("\n\n", Sections);
}

