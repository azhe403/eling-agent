# Session Start MCP Tool Specification (Locked)

> **Superseded by `memory_recall` rename.** This spec documents the historical contract for the `session_start` MCP tool, which has been renamed to `memory_recall` to better reflect what it does. It is preserved as a record of the design decisions (e.g. structured `context` input, trigger-matched intentions, ~200-char truncation, scope `project | global | merged`). The new tool lives in `src/backend/Eling.Core/MemoryRecall/MemoryRecallService.cs` and `src/backend/Eling.Backend/Mcp/Tools/MemoryRecallTool.cs`. See CHANGELOG.md `[Unreleased]` for the rename + bug fix. Do not implement against this spec — it is no longer the source of truth.
>
> Status: Locked (immutable) — at the time of writing. Subsequent renames to `memory_recall` make this spec historical; refer to the new files for current contract.
> Supersedes: `docs/superpowers/specs/2026-08-19-session-context-mcp-tool-design.md`.
> Plan: `docs/superpowers/plans/2026-08-30-session-start-mcp-tool-superseded.md` (also superseded; the original `2026-08-30-session-start-mcp-tool.md` filename was retired when the plan was marked historical).
> Tracker: created 2026-08-30; superseded 2026-09-01.

## 1. Purpose

Define the canonical contract for a **session start MCP tool** in Eling. The tool
hydrates agent context at the beginning of a conversation by returning, in one call:

1. **Recall**: full memories most relevant to the conversation topics.
2. **Recent**: the most recently updated active memories.
3. **Intentions**: outstanding intentions with their trigger-match status.
4. **Stats**: lightweight memory/intention counts.

This mirrors Vestige's `session_start` and supersedes the earlier (never-executed)
`session_context` design, folding in all of its features (trigger matching, stats,
token truncation, structured context input) under the `memory_session_start` name.

## 2. Scope

### 2.1 In-Scope

- New MCP tool `memory_session_start` in `Eling.Mcp`.
- `ISessionStartService` / `SessionStartService` aggregation in `Eling.Application`.
- Extension of the `Intention` model + `IntentionFrontMatter` with an optional
  `Pattern` field to support trigger matching.
- `IIntentionStorage` DI wiring (first-time registration).
- Trigger matching (Topic / FilePattern / TimeBased), stats, and ~200-char truncation.
- Tests for service and MCP layer.

### 2.2 Out-of-Scope

- New intention CRUD MCP tools (existing `IntentionStorage` stays; separate plan).
- Semantic embedding-based search (Eling remains keyword/FTS5 based).
- Async/streaming response; the tool returns synchronously.

## 3. Tool Contract

### 3.1 Name

`memory_session_start`

### 3.2 Input Schema

```json
{
  "type": "object",
  "properties": {
    "context": {
      "type": "object",
      "properties": {
        "filePath": { "type": "string", "description": "Current file being worked on" },
        "topics": { "type": "array", "items": { "type": "string" }, "description": "Current conversation/task topics" },
        "project": { "type": "string", "description": "Project or codebase identifier" }
      },
      "description": "Current task/context. Optional. When omitted, recall is empty."
    },
    "recallLimit": { "type": "integer", "description": "Max recalled memories (default 10)" },
    "recentLimit": { "type": "integer", "description": "Max recent memories (default 10)" },
    "scope": { "type": "string", "description": "project | global | merged (default merged)" }
  }
}
```

### 3.3 Output Schema

```json
{
  "recallMemories": [ { "id": "...", "type": "fact", "status": "active", "content": "...", "tags": [], "createdAt": "...", "updatedAt": "...", "source": "...", "scope": "project" } ],
  "recentMemories": [ { "same-shape-as-recall" } ],
  "intentions": [ { "id": "...", "description": "...", "triggerType": "topic", "tags": [], "matched": false, "expired": false, "updatedAt": "..." } ],
  "stats": { "totalMemories": 0, "activeMemories": 0, "activeIntentions": 0 }
}
```

## 4. Behavior

1. **Recall**: For each non-empty topic in `context.topics`, run `ScopedMemoryService.SearchAsync(topic, scope, recallLimit)`. Collect hits per scope identity (dedup across topics by `{scope}:{id}`), then load each unique memory via `GetByIdAsync`. Only `Active` memories are returned. Order is unspecified beyond "relevant"; dedup is guaranteed. If `context.topics` is empty/null, `recallMemories` is `[]`.

2. **Recent**: Always returned. List active memories for the resolved scope, ordered by `UpdatedAt` descending (ties by `CreatedAt` descending), then `recentLimit`.

3. **Intentions**: Load all intentions, filter to `Active`. Evaluate each against `context` (see Trigger Matching). Each returned intention carries `matched` (triggered now) and `expired` flags. Unexpired intentions with no match still appear with `matched=false`.

4. **Stats**: `totalMemories` = all memories across scope (any status), `activeMemories` = active count, `activeIntentions` = active (non-expired) intention count. Computed from `ListAsync`/`ListAllAsync` data already loaded where possible to avoid redundant I/O.

5. **Truncation**: `recallMemories` and `recentMemories` `content` is truncated to ~200 characters (append `...`) for token economy.

6. **Scope**: Resolved same as `memory_list`/`memory_search` (`project` | `global` | `merged`, default `merged`; project gets priority on merge). Invalid scope throws `ArgumentException`.

## 5. Trigger Matching

`Intention.Pattern` (new optional string) supplies the trigger detail. Matching rules per `TriggerType`:

| TriggerType | Input used | Match condition | Expired condition |
|---|---|---|---|
| `Topic` | `context.topics` | Any topic keyword has case-insensitive overlap with `Pattern` (or tags/description keywords when `Pattern` empty) | `ExpiresAt` in past |
| `FilePattern` | `context.filePath` | `filePath` matches `Pattern` as glob (e.g. `**/*.cs`) | `ExpiresAt` in past |
| `TimeBased` | time | `ExpiresAt` within next 24h | `ExpiresAt` in past |

- `matched` = true when the trigger condition holds AND not expired.
- `expired` = true when `ExpiresAt != null` and `ExpiresAt <= now`.
- When `Pattern` is empty/null for `Topic`, fall back to matching against the
  intention's `Tags` and `Description` keywords.
- Glob matching for `FilePattern` uses a simple pattern-to-regex translation.

## 6. Data Model Extension

### 6.1 `Eling.Core.Intention`

Add optional property:

```csharp
public string? Pattern { get; set; }
```

Serialized to front-matter as `trigger_pattern` (and `Pattern` / `trigger_pattern`)
through `IntentionFrontMatter`. Backward compatible: existing intention markdown
without the field reads as `null`, and writing preserves it.

### 6.2 `IntentionFrontMatter` (`Eling.Application`)

```csharp
[YamlMember(Alias = "trigger_pattern")]
public string? Pattern { get; set; }
```

### 6.3 Storage

`FileSystemIntentionStorage` maps `Pattern` in the serializer/deserializer blocks.
Existing files remain parseable; new files include `trigger_pattern` only when set.

## 7. Components

| Component | Location | Responsibility |
|---|---|---|
| `ISessionStartService` | `Eling.Application/ISessionStartService.cs` | Interface + `SessionStartResult` record |
| `SessionStartService` | `Eling.Application/SessionStartService.cs` | Aggregation: recall, recent, intentions, stats, truncation |
| `SessionStartResult` | `Eling.Application/SessionStartResult.cs` | Result record (ScopedMemory lists + intentions + stats) |
| `SessionStartRequest` | `Eling.Mcp` | Input model (context + limits + scope) |
| `SessionStartResponse` + nested | `Eling.Mcp` | Output DTOs (`SessionStartMemory`, `SessionStartIntention`, `SessionStartStats`) |
| `MemoryTools.SessionStartAsync` | `Eling.Mcp/MemoryTools.cs` | MCP tool `memory_session_start` |
| DI wiring | `Eling.Mcp/McpServiceExtensions.cs` | Register `IIntentionStorage` + `ISessionStartService` |

`SessionStartService` keeps the aggregation logic (application layer) so the MCP
tool stays a thin mapper — preserving clean layer boundaries.

## 8. Error Handling

- Empty `context` (no filePath/topics/project): `recallMemories=[]`, but `recentMemories`
  and `stats` still returned.
- Invalid `scope`: `ArgumentException` (consistent with other memory tools).
- `memory_session_start` called in a host without scoped/session services:
  `InvalidOperationException`.

## 9. Testing

- `tests/Eling.Application.Tests/SessionStartServiceTests.cs`: recall dedup across
  topics, inactive-exclusion from recall, recent ordering + limit, intention
  trigger matching (topic / filePattern / timeBased), expired flag, stats counts,
  truncation length.
- `tests/Eling.Application.Tests/IntentionStorageTests.cs` (extend existing):
  `Pattern` round-trips through front-matter; missing field reads `null`.
- `tests/Eling.Mcp.Tests/MemoryToolsTests.cs`: tool delegates to service, maps DTOs,
  rejects when session service unavailable.
