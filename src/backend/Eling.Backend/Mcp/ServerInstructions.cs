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

        "Memory Save & Scope Rules: Default to scope=project for repo-specific architecture decisions, local conventions, and project-bound lessons. Use scope=global for general coding knowledge, cross-project preferences, language snippets, or global user guidelines. Write memory content in English by default (or the user's preferred native language if requested), but ALWAYS use lowercase English tags for consistent indexing.",

        "Consistency Rules: Use clean lowercase English memory tags (no underscores, single words/concise phrases). Keep memories portable — avoid machine-specific absolute paths and personal usernames; use relative paths or generic placeholders so memories stay consistent across machines and platforms.",

        "Memory Recall Strategy: Trigger `memory_recall` on clear phase transitions — (1) Before starting a new task/spec implementation to check project rules, (2) Before preparing git commits to verify repo hygiene, (3) When debugging recurring errors/failures, or (4) On explicit user recall ('eling <topic>'). DO NOT recall on greeting/casual chat or repeated micro-steps in the same task to preserve context window and latency.",

        "Project Scope Initialization: Local project memory requires explicit user consent before initializing. Check project posture via `memory_project_status` (or `projectScope.adoptable` in recall results) before the first project save. If `adoptable: true`, PROACTIVELY ask the user for consent before calling `memory_init_project`. If policy is disabled, route saves to scope=global without prompting for init.",

        "Tool responses carry provenance: `projectName`/`projectRoot` identify the scope level a memory lives in (own, an ancestor, or null for global); `memory_get`/`list`/`search` return scoped payloads."
    ];

    /// <summary>
    /// Merged instruction text joined by double newlines for clear paragraph separation.
    /// </summary>
    public static readonly string Default = string.Join("\n\n", Sections);
}

