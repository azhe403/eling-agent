# Session Context MCP Tool Design

## Goal

Add a `session_context` MCP tool to `Eling.Mcp` that mirrors the behavior of Vestige's `session_start`: given the current task context (file, topics, project), return recent memories, matching search results, health statistics, and any triggered intentions — so the agent starts a session with relevant context already loaded.

## Dependencies

- `Eling.Application` (existing `IMemoryService` for memories)
- New `Eling.Application.IIntentionService` (see Intention Service Design section)
- Existing `Eling.Index` FTS5 search (via `IMemoryService.SearchAsync`)

## MCP Tool Signature

Tool name: `session_context`

Input schema:
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
      }
    },
    "limit": { "type": "integer", "description": "Max recent memories to return (default 10)" }
  }
}
```

Output:
```json
{
  "recentMemories": [
    {
      "id": "01KZXVQS3ATVGN75GK90MCBYVK",
      "type": "fact",
      "content": "PostgreSQL port is 5432...",
      "tags": ["database", "config"],
      "updatedAt": "2026-08-13T15:27:02+00:00"
    }
  ],
  "searchResults": [
    {
      "id": "...",
      "score": 3.2,
      "content": "..."
    }
  ],
  "intentions": [
    {
      "id": "...",
      "description": "Check DB migration status",
      "trigger": { "type": "topic", "pattern": "database,migration" },
      "matched": true,
      "expired": false
    }
  ],
  "stats": {
    "totalMemories": 58,
    "activeMemories": 40,
    "activeIntentions": 3
  }
}
```

## Behavior

1. **Recent memories**: Always returned (up to `limit`), newest first by `UpdatedAt`.
2. **Search results**: When `topics` or `project` are provided, run FTS5 search per topic (joined OR) using existing `IMemoryService.SearchAsync`. Return top results.
3. **Intentions**: Load all active intentions via `IIntentionService.ListActiveAsync()`. Evaluate each against the input context:
   - `Topic` trigger: any topic keyword in `topics` contains/overlaps trigger pattern keyword
   - `FilePattern` trigger: `filePath` matches glob pattern
   - `TimeBased` trigger: `ExpiresAt` is not null and is within the next 24h (upcoming) OR has passed (expired)
   - Result marks each intention `matched` (triggered now) or `expired`
4. **Stats**: Count total and active memories (from `ListAllAsync`), and active intentions.
5. Return `recentMemories` previews truncated to ~200 chars for token economy.

## New DTOs (`Eling.Mcp` or `Eling.Server/Dtos`)

Place in `Eling.Mcp/Models/` since this is an MCP-focused feature:
- `SessionContextRequest` — input model
- `SessionContextResponse` — output model (or anonymous types serialized directly)

## Intention Service Design (`Eling.Application`)

### `IIntentionService` Interface

```csharp
using Eling.Core;

namespace Eling.Application;

public interface IIntentionService
{
    Task<Intention> SaveAsync(string description, IntentionTrigger trigger, DateTimeOffset? expiresAt = null);
    Task<Intention?> GetByIdAsync(MemoryId id);
    Task<bool> DeleteAsync(MemoryId id);
    Task<IReadOnlyCollection<Intention>> ListActiveAsync();
    Task<IReadOnlyCollection<Intention>> ListAllAsync();
}
```

### `IntentionService` Class

- Wraps `IIntentionStorage`
- `SaveAsync` — creates new `Intention` (Id auto-generated), validates trigger pattern non-empty, persists
- `DeleteAsync` — delegates to storage, returns bool
- `ListActiveAsync` — filters storage list by `Status == Active`
- Trigger evaluation helper (used by session_context): `static bool Matches(IntentionTrigger, SessionContextRequest)`

## Tests (`tests/Eling.Application.Tests/IntentionServiceTests.cs`)

- Save creates and persists intention
- GetById returns saved intention
- Delete removes and returns true
- ListActive filters by status
- Trigger matching: topic keyword hit, file pattern glob hit, time-based expiry
