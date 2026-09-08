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
        "Eling is a durable markdown-backed memory system. Memory Markdown files under '.eling/memories/' are the canonical source of truth and MUST be tracked in Git.",

        "Generated runtime files (logs, '.eling/index.db*', '.eling/*.db', '.eling/*.db-journal', '.eling/*.db-wal', '.eling/runtime/') MUST be added to '.gitignore'. When running in a project workspace, always check that '.gitignore' ignores these runtime files while keeping '.eling/memories/' tracked; prompt the user for confirmation to fix '.gitignore' if needed.",

        "'Eling <something>' (Javanese for 'remember <something>') is a recall instruction: retrieve the memory previously stored in Eling (e.g. 'eling build steps' recalls the build steps saved in memories).",

        "All memory operations MUST use Eling MCP tools — never read, search, or write '.eling/memories/' files directly.",

        "Memory Save Rule: ALWAYS use scope=project by default so the memory is written to '.eling/memories/' and survives session changes; use scope=global only for cross-project rules.",

        "Consistency Rules: Use clean lowercase memory tags (no underscores, no mixed language). Keep memories portable — avoid machine-specific absolute paths and personal usernames; use relative paths or generic placeholders so memories stay consistent across machines and platforms.",

        "Memory Recall Strategy: Hydrate context at session start, before significant actions, and after major milestones; avoid redundant recall on every micro-turn to preserve latency and context window."
    ];

    /// <summary>
    /// Merged instruction text joined by double newlines for clear paragraph separation.
    /// </summary>
    public static readonly string Default = string.Join("\n\n", Sections);
}

