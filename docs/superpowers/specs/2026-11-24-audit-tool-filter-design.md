# Audit Tool Filter — Design

> **Status:** Proposed — awaiting approval. **Date:** 2026-11-24.
> **Repo:** eling-agent. **Owner:** this session (managed doc, local only).
> **Related:** `docs/superpowers/specs/2026-09-11-audit-trail-design.md` (amendments B4, B5, §7.3, §13).

## 1. Intent and why

Only 19 of the 71 MCP tool registrations produce an entry in the audit trail; 52 do not, including `recall`. The cause is structural: audit entries are written by explicit `IAuditLogger.RecordAsync` calls inside tools and domain services, not by the tool-call transport. Wiring the remaining tools one by one is scattered work, and it gives each tool a different — usually absent — execution-time measure.

The goal is one central place that observes every MCP tool call, records a single audit entry per call, and measures execution time uniformly from before dispatch to after completion. A mediator framework was considered and explicitly rejected by the user: it would require turning ~71 tools into request/handler pairs, would not improve audit durability (the same `BufferedAuditLogger` would still be the writer), and would not cover surfaces the filter cannot see.

## 2. Goals

- Every one of the 71 `[McpServerTool]` calls over the stdio MCP host is observed by one filter.
- Exactly one audit entry per call: no duplicates with the 19 existing domain hooks.
- `DurationMs` measured as tool execution time (before `next` → after `next`, in `finally`).
- No tool class is edited; the change is filter + registration + a small ambient marker.
- Only whitelisted argument metadata reaches the entry (no file contents, credentials, connection strings, or unbounded query text).

## 3. Non-goals and anti-goals

- No MediatR or any other mediator dependency.
- Not covering the built-in agent's internal tool calls (`RecallAgentTool` calls `RecallTool.RecallAsync` directly, bypassing the MCP pipeline) or dashboard REST endpoints in this iteration. Both are separate surfaces and would need their own hook that reuses the same recorder.
- Not changing the durability contract: access-trail entries remain fire-and-forget (design §7.3); losing the tail on abrupt exit stays accepted.
- Not emitting raw transport payloads (design B5) and not recording content (design §13).
- Anti-goal: classifying every call as a Change entry. Only tools that actually mutate state go to the change trail; everything else stays on access.

## 4. Constraints

- `AddElingMcpServerStdio` installs filters in this order (outermost first): `ToolTelemetryFilter`, `AuditActorFilter`, `ToolPolicyFilters`, `ToolExceptionFilter`, `HarvestNudgeFilter`. The actor scope is `AsyncLocal`, opened by `AuditActorFilter`.
- `ToolExceptionFilter` converts handler exceptions into `CallToolResult` with `IsError = true`, so an outer filter sees a completed call.
- `AuditEvent` already carries `Trail`, `Category`, `Action`, `Actor` (ambient), `Source`, `Target`, `Outcome`, `DurationMs`, `Query`, `ResultCount`.
- `BufferedAuditLogger.RecordAsync` is fail-open; `NullAuditLogger` records nothing.
- Repo conventions: one type per file, no tuple shapes, file-scoped namespaces.

## 5. Approaches considered

**A — Filter with domain-aware fallback (chosen).** One `McpRequestFilter` wraps every call. Pre starts a stopwatch. In `finally`, it checks an ambient marker: if a domain hook already wrote an entry during this call, the filter writes nothing; otherwise it writes one generic entry with tool name, duration, outcome, and whitelisted target. Covers all 71 tools without touching them, keeps the 19 rich entries intact, and produces no duplicates. Cost: a small `AsyncLocal` marker that the real logger sets on successful enqueue.

**B — Filter writes every call.** Simpler, but `recall` would get both a `recall` entry and the existing `memory_search` entry, and it contradicts design B5's deliberate non-goal of transport-level logging. Rejected.

**C — MediatR pipeline behavior.** ~71 command types plus handlers; "one place" is the behavior, but it adds a dependency and migration cost without improving durability or surface coverage. Rejected by the user.

## 6. Detailed design

### 6.1 Filter and placement

New type `AuditToolFilter` in `Eling.Backend/Mcp/Filters/AuditToolFilter.cs`, exposing `Create(IAuditLogger? logger = null)` that returns `McpRequestFilter<CallToolRequestParams, CallToolResult>`. Register it in `AddElingMcpServerStdio` **after** `AuditActorFilter` and **before** `ToolPolicyFilters`:

```
telemetry → actor → audit → policy → exception → harvest → handler
```

Rationale: the ambient actor is already open; policy-denied calls still get an entry; exceptions are normalized to `IsError` by the inner filter before the post step reads the result.

### 6.2 Timing and outcome

- Start `Stopwatch` before `next`; `await next(context, ct)`.
- In `finally`: stop, and if nothing marked the call, `RecordAsync` with `DurationMs`.
- `Outcome` is `success`, `error` (from `result.IsError` or exception), or `denied` when the policy filter rejected the call. `ToolPolicyFilters.CreateCallFilter` sets an ambient denial marker before returning `IsError`, so the post step does not match user-facing text.
- Duration covers inner filters and the handler; it is tool execution time, not the full MCP round trip (the stopwatch stops before the audit write).
- A generic entry on the **change** trail is awaited with `CancellationToken.None` so its write confirmation is not skipped on an exception path; entries on the **access** trail are fire-and-forget.

### 6.3 De-duplication marker

New type `AuditCallMarker` (separate file) holding an `AsyncLocal` scope.

- The filter opens a marker scope for the call.
- `BufferedAuditLogger.RecordAsync` calls `AuditCallMarker.MarkRecorded(action)` only after a successful channel enqueue. `NullAuditLogger` never marks.
- The post step writes only when no entry with the **same action as the tool name** was recorded. A nested hook with a different action does not suppress the tool's own entry.
- Consequence 1: `recall` gets its own `recall` entry with measured duration; the nested `memory_search` entry from `ScopedMemoryService` remains as a separate domain fact.
- Consequence 2: a tool whose own hook fires only on some paths (e.g. `CodebaseSearchTool`'s empty-query early return) gets a generic entry on the unrecorded paths — intended hole-filling.

### 6.4 Tool classification

New type `McpToolAuditMetadata` (separate file): a static table keyed by **(tool name, declaring type)** — the pair is needed because `doc_relation` is registered twice, read in `DocsReadTool` and write in `DocsWriteTool`. Each row carries `AuditCategory`, `AuditTrail`, and safe argument keys for `Target`/`Query`. Generic entries reuse existing `AuditCategory` members (filesystem tools → `Filesystem`, memory → `Memory`, codebase → `Codebase`, DB/ES/Redis → `Database`, docs/skill/commit/shell/tools-policy/recall → `Runtime`); no enum, schema, or dashboard-filter change.

Mutating tools → **change** trail: `file_write`, `file_edit`, `file_append`, `file_delete`, `file_move`, `file_copy`, `directory_*`, `memory_save`, `memory_delete`, `memory_copy_to_project`, `memory_promote_to_global`, `memory_init_project`, `memory_project_scope_policy`, `memory_rebuild_index`, `memory_maintenance`, `doc_save`, `doc_annotate`, `doc_promote`, `doc_index`, `doc_scope_policy`, `doc_offer_managed`, `skill_save`, `skill_delete`, `skill_adopt`, `skill_restore`, `tools_policy`, `commit_lease`, `commit_release`, `db_import_connections`, `shell_execute`. All others → **access**.

Tools with an existing domain hook are skipped by the marker, so the table mainly drives the 52 uncovered tools.

### 6.5 Argument privacy

The filter receives raw `CallToolRequestParams.Arguments`. It must never serialize the whole object. Only whitelisted keys from `McpToolAuditMetadata` are read, string values capped (proposed 200 characters), and file contents, connection strings, and command text are never included. `Query` is populated only for search-class tools.

## 7. Verification (to be specified in the plan)

- A tool with no own-name hook (`recall`) yields its own `recall` entry with duration and outcome, while any nested domain entry stays separate.
- A tool with an existing hook does not get a second entry.
- `BufferedAuditLogger` marks; `NullAuditLogger` does not.
- Policy-denied and exception-throwing calls both produce an entry with the right outcome.
- Whitelisted metadata only — no raw arguments in the entry.
- Existing MCP tool tests remain green.

## 8. Left to the builder

- Final per-tool classification values in `McpToolAuditMetadata` (mechanical, against the 71-name inventory).
- Whether to add an equivalent hook for the in-app agent and REST endpoints in a later change.

## 9. Resolved during review

- The marker is action-aware, so `recall` is no longer hidden behind its nested `memory_search` (reviewer question 1).
- Generic entries reuse existing categories; no schema change (question 2).
- Denial is signalled by an ambient marker in the policy filter, not by text matching (question 3).
- Change-trail generic entries are awaited with `CancellationToken.None`; access entries are not (question 4).
- The metadata key includes the declaring type so both `doc_relation` registrations classify correctly (question 5).
