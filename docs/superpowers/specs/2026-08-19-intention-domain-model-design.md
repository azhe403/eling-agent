# Intention Domain Model Design

## Goal

Implement the `Intention` domain model in `Eling.Core` to support session context triggering. Intentions represent ephemeral task-control logic (e.g., "remind me to check database migration status when working on Eling.Data project") as opposed to memories which hold persistent factual content.

## Dependencies

- `MemoryId` from `Eling.Core` (reuse for Intention ID generation)

## Domain Types (`Eling.Core`)

### `IntentionTriggerType` Enum

- `Topic` - Triggered when conversation topics match specified keywords
- `FilePattern` - Triggered when working with files matching a glob pattern
- `TimeBased` - Triggered when a specified time period has elapsed

### `IntentionTrigger` Value Object

Properties:
- `IntentionTriggerType Type`: The trigger mechanism
- `string Pattern`: The matching pattern:
  - For `Topic`: Comma-separated keywords (e.g., "database,migration,postgres")
  - For `FilePattern`: Glob pattern (e.g., "**/*.cs", "src/backend/**")
  - For `TimeBased`: ISO 8601 duration or "until YYYY-MM-DDTHH:mm:ssZ"

### `IntentionStatus` Enum

- `Active` - Intention is live and will trigger when conditions are met
- `Superseded` - Intention has been replaced by a newer version
- `Archived` - Intention is inactive but retained for history

### `Intention` Model

Properties:
- `MemoryId Id`: Unique identifier (reuses MemoryId/ULID)
- `string Description`: Human-readable description of what to remember (required)
- `IntentionTrigger Trigger`: The trigger condition (required)
- `IntentionStatus Status`: Lifecycle status (default `IntentionStatus.Active`)
- `DateTimeOffset CreatedAt`: When the intention was created
- `DateTimeOffset ExpiresAt?`: Optional expiration timestamp (null = never expires)

Factory/Constructor:
```csharp
public Intention(
    string description,
    IntentionTrigger trigger,
    DateTimeOffset? expiresAt = null)
```

- `Id` auto-generated via `MemoryId.NewId()`
- `Status` defaults to `IntentionStatus.Active`
- `CreatedAt`/`UpdatedAt` set to `DateTimeOffset.UtcNow`

## Tests (`tests/Eling.Core.Tests/IntentionTests.cs`)

- Creation with defaults (`Status == Active`, `Id` is valid ULID, timestamps set)
- Property assignments (`Description`, `Trigger`, `ExpiresAt`)
- Topic trigger pattern validation
- FilePattern trigger pattern validation
- TimeBased trigger with duration string
- Status transitions (`Active` -> `Superseded`, `Active` -> `Archived`)

## Notes

- Intentions are separate from memories: intentions are ephemeral trigger metadata, memories are persistent factual content
- Intentions can reference memories via description text, but are not directly linked
- Intentions should be garbage-collected when expired or archived; memories are version-controlled in Git
