# Agent Prompt Diet & Context Injection — Design

Date: 2026-11-19
Status: draft (awaiting approval)
Scope: `src/backend/Eling.Backend/Agent/**`, `tests/Eling.Backend.Tests/AgentChatTests.cs`

## Intent & why

The Eling Desktop agent ("agentic desktop nyata" direction) runs its own loop
inside the backend. Today its system prompt is a two-line identity plus the
entire `ServerInstructions.Default` — the text written for *external MCP
clients* during the initialize handshake. That text is long, MUST-heavy, and
half of it (git commit-lease protocol, MCP handshake framing) duplicates what
tool descriptions already carry; the parts that matter for embedded behavior
(edit-vs-overwrite, verify-before-claim, stop-when-stuck) are diluted.

The user picked "prompt diet dulu": fix the embedded agent's behavior through
a dedicated, tight prompt and automatic project-context injection — measurable
behavior improvement without rewriting the loop. No bridging of external
agent engines for now (explicit user decision 2026-11-19).

## Goals

1. A dedicated embedded-agent system prompt, separate from `ServerInstructions`
   (which stays untouched for MCP clients).
2. Hard operating rules with few-shot examples: edit-not-overwrite,
   verify-before-claim, fail-twice-then-stop/ask, workspace-bound honesty.
3. First-turn project context injection: workspace `AGENTS.md` (truncated) and
   git branch + dirty status.
4. Tests pinning the prompt shape (capture-gateway assertions).

## Non-goals / anti-goals

- No changes to the loop mechanics (hop cap, history cap, fallback).
- No compaction/summarization (follow-up phase).
- No behavior eval harness yet — user opted to do prompt diet first; eval
  harness follows after.
- No changes to `ServerInstructions.Default` or the MCP surface.
- No permission/approval system changes.

## Constraints

- Eling is a public repo; no third-party harness names in source or tests
  (existing convention).
- The desktop agent's tools are the same `IAgentTool` set; prompt claims must
  match reality (e.g. `file_edit` exists for existing files).
- Auto-recall injection on first turn already exists in `AgentTurnService`;
  context injection rides the same path, only on first turn.
- Desktop app work follows the standing rule: kill + rebuild + relaunch and
  report actual verification output.

## Approaches considered

1. **Prompt diet (chosen).** Separate prompt constant; tighten; add rules +
   few-shot + context injection. Fast, low risk, low test surface.
2. Eval harness first (rejected for now by user): measure before fixing.
   Correct long-term, slower to first improvement.
3. Bridge external opencode engine (rejected for now by user): inherits a
   mature agent core but makes desktop an opencode client; contradicts the
   "native & ringan" stance and the "focus on Eling engine" decision, and
   reintroduces a Bun runtime dependency.
4. Status quo + manual tuning (rejected): unmeasurable; the current wall of
   MUST text stays.

## Design

### 1. New prompt source

New internal static `AgentSystemPrompt` (next to `AgentTurnService` or in
`Agent/Services/`): builds the embedded-agent prompt. `AgentIdentity` moves
there (tightened); `AgentTurnService` calls it instead of
`string.Format(AgentIdentity, workspace) + ServerInstructions.Default`.

Sections, in order:

1. **Identity & scope** — one line identity; "operate only inside this
   workspace: {workspace}"; honesty ("if a tool reports an error, report it;
   never invent output").
2. **Operating rules (hard)** — numbered, testable:
   1. New files only via `file_write`. Modify existing files with `file_edit`,
      never `file_write`/`file_delete` without explicit user instruction.
   2. Never claim done until verified: re-read the changed file or run the
      project's verification (build/tests) when the change affects code;
      report the actual result.
   3. If a tool fails, read the error, adapt once or twice, then stop and
      report — do not retry the identical call in a loop.
   4. If blocked or uncertain (missing file, ambiguous intent, destructive
      action), ask instead of guessing.
   5. Prefer dedicated tools over `shell` for file reads/search
      (`file_read`, `codebase_search`, `glob`); `shell` for build/test/git.
   6. Keep changes minimal; match the project's existing style; do not
      refactor outside the ask.
   7. Git: before `git add`/`commit` in this checkout call `commit_lease`;
      `commit_release` when it lands or the attempt fails. Do not bypass or
      unstage other sessions' staged files. (One paragraph — the tool
      descriptions carry the rest; drop the full MCP wall-of-text.)
3. **Memory & tools brief** — recall already ran server-side on the first
   turn (don't redundantly re-recall); persist durable knowledge via
   `memory_save` when it outlives the chat; prefer `codebase_search` over
   `glob`/`grep`-style sweeps for concept queries; read only returned line
   ranges.
4. **Few-shot** — three compact examples:
   (a) change an existing function → `file_read` → `file_edit` → rebuild →
   report output; (b) a tool returns an error twice → stop, report, ask;
   (c) claim "done" after edit → verify first, then report. Format: bare
   text, ~15 lines total.
5. **Project context (first turn only)** — injected by the existing
   first-turn branch alongside auto-recall: workspace-root `AGENTS.md`
   (first ~2000 chars, labeled, silently skipped when absent) and one line
   `git: branch=<name>, dirty=<count of porcelain entries>` via the existing
   `GitStatusReader.TryRead` (null → omitted; never fails the turn).

### 2. Wiring

- `AgentTurnService.RunTurnAsync`: replace
  `string.Format(AgentIdentity, workspace) + ServerInstructions.Default`
  with `AgentSystemPrompt.Build(workspace)`.
- First-turn branch: after the recall block, append `AGENTS.md` section and
  the git line when available. Same try/catch isolation as recall — injection
  failure logs a warning and never breaks the turn.
- The MCP `ServerInstructions` path is untouched (still used by the MCP
  server for external clients).

### 3. Tests (`tests/Eling.Backend.Tests/AgentChatTests.cs`)

Extend the existing `FakeGateway` with a variant capturing each
`SingleShotRequest` (prompt + history). Assert:

- Prompt contains the operating rules (e.g. `file_edit`, `commit_lease`) and
  does NOT contain MCP handshake framing (e.g. `initialize handshake`).
- First turn for a workspace with `AGENTS.md` contains its content (truncated);
  second turn does not re-inject it.
- Workspace without `AGENTS.md` produces a valid turn (no exception).

All existing `AgentChatTests` must stay green.

## Size

One focused project: ~3 source files touched plus tests. Half a day.

## What's left to the builder

- Exact wording of rules and few-shots (must stay compact).
- Whether `AgentSystemPrompt` lives beside `AgentTurnService` or in its own
  file under `Agent/Services/`.
- Whether to include a one-line note about `shell_execute` policy, or rely on
  its tool description alone (builder's call, note it in the PR).
- Truncation length for AGENTS.md (default 2000 chars; builder may adjust with
  a comment justifying it).
