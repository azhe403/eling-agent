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

        "Generated runtime files MUST be added to '.gitignore': logs, the runtime directory '.eling/runtime/', and every SQLite database plus its sidecars. Sidecars are always '<name>.db-<suffix>' — '-journal', '-wal', '-shm', '-wal2' — so prefer the patterns '*.db' and '*.db-*' over naming each suffix; that stays complete as SQLite adds more and does not over-match unrelated names like '*.dbf'. When running in a project workspace, always check that '.gitignore' ignores these runtime files while keeping '.eling/memories/' tracked; prompt the user for confirmation to fix '.gitignore' if needed.",

        "'Eling <something>' (Javanese for 'remember <something>') is a recall instruction: retrieve the memory previously stored in Eling (e.g. 'eling build steps' recalls the build steps saved in memories).",

        "All memory operations MUST use Eling MCP tools — never read, search, or write '.eling/memories/' files directly.",

        "Memory Save & Scope Rules: Default to scope=project for repo-specific architecture decisions, local conventions, and project-bound lessons. Use scope=global for general coding knowledge, cross-project preferences, language snippets, or global user guidelines. Write memory content in English by default (or the user's preferred native language if requested), but ALWAYS use lowercase English tags for consistent indexing.",

        "Consistency Rules: Use clean lowercase English memory tags (no underscores, single words/concise phrases). Keep memories portable — avoid machine-specific absolute paths and personal usernames; use relative paths or generic placeholders so memories stay consistent across machines and platforms.",

        "Memory Recall Strategy: Trigger `memory_recall` on clear phase transitions — (1) Before starting a new task/spec implementation to check project rules, (2) Before preparing git commits to verify repo hygiene, (3) When debugging recurring errors/failures, or (4) On explicit user recall ('eling <topic>'). DO NOT recall on greeting/casual chat or repeated micro-steps in the same task to preserve context window and latency.",

        "Mandatory Session-Start Recall (HARD RULE): Before any response or tool call in the first user turn of every new session, invoke `memory_recall` with `topics` derived from the opening message. Do this BEFORE calling `memory_project_status`, inspecting files, or writing any reply — even when the opening message looks casual or ambiguous. This makes recall the default behavior; users do not need to configure `AGENTS.md` globally.",

        "Mandatory Save (HARD RULE): Whenever the turn contains something meant to outlive this conversation, call `memory_save` BEFORE replying. Tier 1 — what the user states: a standing rule or preference ('always', 'never', 'from now on', 'prefer X', 'stop doing Y'), a correction of a fact, convention, or past mistake, a decision and its rationale, or an explicit 'remember this' / 'ingat ya'. Tier 2 — what you observe while working: a project or environment fact that is NOT derivable from the tree (a gotcha, a platform/port/tooling quirk, a wrong assumption the evidence just disproved, where something actually lives, which of two competing ways is the real one). Do not wait for the words 'remember' or 'ingat' when Tier 1 is present; infer the trigger. An acknowledgement is never a substitute: replying 'okay', 'noted', 'siap', 'sure' WITHOUT a `memory_save` call is a failure, not a save — acknowledge and save, the two are additive. SKIP transient task state, speculation, and anything unverified. SKIP anything the repository already records (AGENTS.md, code, specs, README, git history) — the tree remembers those, and a memory that restates them is noise. SKIP a near-duplicate of a memory `memory_recall` already surfaced this turn. Map the tier to `type`: Tier 1 rules and preferences → `preference`, corrections → `lesson`, decisions → `decision`, Tier 2 observations → `fact`. Record how far the observation was verified (which file, which run, which platform) so a later agent knows where it stops holding.",

        "Project Scope Initialization: Local project memory requires explicit user consent before initializing. Check project posture via `memory_project_status` (or `projectScope.adoptable` in recall results) before the first project save. If `adoptable: true`, PROACTIVELY ask the user for consent before calling `memory_init_project`. If policy is disabled, route saves to scope=global without prompting for init.",

        "Tool responses carry provenance: `projectName`/`projectRoot` identify the scope level a memory lives in (own, an ancestor, or null for global); `memory_get`/`list`/`search` return scoped payloads.",

        "File Operation Preference: For file operations inside the workspace, PREFER Eling's sandboxed file tools over host-native equivalents (`file_read`, `file_search`, `glob`, `directory_list`, `path_test`, `file_edit`, `file_write`, `file_append`, `file_move`, `file_copy`, `file_delete`, `directory_create`, `directory_delete`). Resolve paths relative to the workspace root reported by `workspace_root`. These tools are audited via telemetry and enforce the sandbox; pass `lineNumbers=true` to `file_read` for line-numbered output. This is a preference, not a mandate — host-native tools and read-only `allowExternal=true` remain acceptable fallbacks, especially when the operation is outside the project root or the host tool is materially easier in context.",

        "Codebase Indexing (HARD RULE): Eling maintains a per-workspace FTS index of the working directory — the folder the backend was launched in, never the nearest `.eling` ancestor — (porter + trigram, BM25-ranked) so agents can search code without opening a terminal. Tools: `codebase_index` (build/refresh; incremental by default, `full:true` rebuilds, `paths` scopes to subfolders), `codebase_search` (query plus line ranges plus snippets ready to inject; `scope:'project'` default (this workspace) or `'all'` for every alive workspace, `projects:[...]` for explicit roots, `pathPrefix`/`filePattern` to narrow), `codebase_status` (file/chunk counts, last indexed time, watcher state, owning workspace root). In the first user turn of every new session, call `codebase_status` alongside `memory_recall` and BEFORE any code search or file inspection. If no index exists (fileCount 0), ASK the user for approval to run `codebase_index` and wait for the answer; never start indexing without consent. If the index is stale (lastIndexedAt is not today, or pendingFiles > 0), also ask before refreshing.",

        "Codebase Search (HARD RULE): When searching code, symbols, implementations, or text across the workspace, ALWAYS call `codebase_search` first. DO NOT use native `grep`, `file_search`, or shell commands as the primary lookup tool — bypassing `codebase_search` when an index exists is a DIRECT RULE VIOLATION. `codebase_search` provides ranked BM25 + trigram snippets with line ranges that save tokens and search latency. Native `grep` is strictly a secondary fallback, permitted ONLY when: (1) `codebase_search` explicitly returns 0 hits, (2) the query requires complex regex unsupported by SQLite FTS, or (3) codebase index creation was denied by the user.",

        "Codebase Index Notes: The index is a rebuildable cache (deleting the DB loses nothing — just re-run `codebase_index`); a debounced background watcher keeps it fresh on the dashboard owner, so after edits made outside the agent's own writes, allow a moment for the batch to drain or re-run `codebase_index`, and check `codebase_status` freshness. `.gitignore` rules are honored on top of the built-in excluded dirs. `codebase_search` never throws on a missing index — it returns empty hits with a hint to index first.",
    ];

    /// <summary>
    /// Merged instruction text joined by double newlines for clear paragraph separation.
    /// </summary>
    public static readonly string Default = string.Join("\n\n", Sections);
}

