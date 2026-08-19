# Intention MCP Tools Design

## Goal

Add `intention_save`, `intention_get`, `intention_list`, `intention_update`, `intention_delete` MCP tools to `Eling.Mcp`, mirroring the existing `MemoryTools` pattern. These manage the ephemeral intentions used by `session_context`.

## Dependencies

- `Eling.Application.IIntentionService` (new)
- Existing MCP infrastructure (`McpServerToolType`, `McpServerTool`, `WithToolsFromAssembly`)

## New File: `Eling.Mcp/IntentionTools.cs`

Class annotated `[McpServerToolType]`, constructor-injected `IIntentionService` and `IMemoryService` (for session_context) — same pattern as `MemoryTools`.

### Tool: `intention_save`

Create an intention.

Parameters:
- `description` (string, required) — what to remember
- `triggerType` (string, enum: `topic` | `filePattern` | `timeBased`, required)
- `pattern` (string, required) — keywords/glob/duration depending on type
- `expiresAt` (string?, ISO 8601, optional — null = never expires)

Returns: `{ id, description, trigger: { type, pattern }, status, createdAt, expiresAt }`

### Tool: `intention_get`

Retrieve an intention by ID.

Parameters:
- `id` (string ULID, required)

Returns the full intention or null.

### Tool: `intention_list`

List intentions, filtered by status.

Parameters:
- `status` (string, enum: `active` | `superseded` | `archived` | `all`, default `active`)

Returns array of intentions (id, description, trigger, status, expiresAt).

### Tool: `intention_update`

Update an existing intention. Only provided fields change (same semantics as `memory_update`).

Parameters:
- `id` (string ULID, required)
- `description` (string?, optional)
- `triggerType` (string?, optional)
- `pattern` (string?, optional)
- `status` (string?, enum, optional)
- `expiresAt` (string?, optional)

Returns the updated intention.

### Tool: `intention_delete`

Delete an intention by ID.

Parameters:
- `id` (string ULID, required)

Returns `true`/`false`.

## DI Wiring (`McpServiceExtensions.cs`)

Add to `AddElingCoreServices`:

```csharp
// Intentions
services.AddScoped<IIntentionService, IntentionService>();
services.AddSingleton<IIntentionStorage>(
    new FileSystemIntentionStorage(RepositoryRoot.Find(Environment.CurrentDirectory)));
```

Register with DI alongside existing memory services. `IntentionTools` auto-discovered via `WithToolsFromAssembly()` (same assembly as `MemoryTools`).

## Lifecycle Notes

- `intention_update` with `status=archived` or `status=superseded` removes it from future `session_context` triggers (ListActiveAsync filters)
- Expired intentions surface in `session_context` with `expired: true` so the agent can archive them via `intention_update`
- No separate "gc" tool for now — deletion is explicit via `intention_delete`

## Tests (`tests/Eling.Mcp.Tests/IntentionToolsTests.cs`)

- Expose `IntentionTools` with fake service (same FakeIntentionService/FakeIntentionStorage inner-class pattern as MemoryTools tests)
- Each tool returns expected output shape
- `session_context` returns recentMemories + stats + intentions
- Invalid ULID returns error
- Missing required field returns error