# Intention Storage Design

## Goal

Implement file-system persistence for `Intention` objects in `Eling.Storage`, mirroring the existing `FileSystemMemoryStorage` pattern. Intentions are stored as YAML-front-matter files in `.eling/intentions/`, separate from memories.

## Dependencies

- `Eling.Core` (already referenced)
- `YamlDotNet` (already in `Eling.Storage`)
- No new dependencies

## Storage API Contract (`Eling.Storage`)

### `IIntentionStorage` Interface

```csharp
using Eling.Core;

namespace Eling.Storage;

public interface IIntentionStorage
{
    Task SaveAsync(Intention intention);
    Task<Intention?> GetByIdAsync(MemoryId id);
    Task<bool> DeleteAsync(MemoryId id);
    Task<IReadOnlyCollection<Intention>> ListAllAsync();
}
```

### `FileSystemIntentionStorage` Class

Implements `IIntentionStorage`.

- Constructor: `FileSystemIntentionStorage(string rootPath)` (defaults to base directory)
- Safe path resolution: `Path.Combine(rootPath, "intentions", $"{id.Value}.md")`
- Path traversal check: throws `ArgumentException` if resolved path escapes `Path.Combine(rootPath, "intentions")`
- Root path discovery: uses existing `RepositoryRoot.Find()` helper from `Eling.Core`

## Serialization Format

Files saved as:

```markdown
---
id: <ULID>
description: <Description>
trigger_type: <Topic|FilePattern|TimeBased>
pattern: <Pattern>
status: <Active|Superseded|Archived>
created_at: <ISO 8601>
expires_at: <ISO 8601|null>
---
```

YAML front matter maps:
- `id` -> `Intention.Id.Value`
- `description` -> `Intention.Description`
- `trigger_type` -> `Intention.Trigger.Type` (lowercase string)
- `pattern` -> `Intention.Trigger.Pattern`
- `status` -> `Intention.Status` (lowercase string)
- `created_at` -> `Intention.CreatedAt` (ISO 8601 offset format)
- `expires_at` -> `Intention.ExpiresAt` (null omitted or null output)

## Reuse of Existing Infrastructure

- `MemoryFrontMatter` YamlDotNet configuration (UnderscoredNamingConvention) is reused
- `LegacyTolerantDateTimeOffsetConverter` reused for timestamp parsing
- Intention files are tracked in Git like memories (`.eling/intentions/` NOT in `.gitignore`)
- `.eling/index.db` and `.eling/logs/` remain gitignored runtime artifacts

## Intention Expiry

- `ListAllAsync` returns all intentions regardless of expiry
- Expiry filtering happens at the service/MCP layer: expired intentions (where `ExpiresAt < UtcNow`) are reported in `session_context` results as "expired" rather than silently dropped, so callers can archive them
- Garbage collection of expired intentions is a manual operation (`intention_delete` or future maintenance tool)

## Tests (`tests/Eling.Storage.Tests/FileSystemIntentionStorageTests.cs`)

- Save round-trips intention with all fields
- Get by ID returns correct intention
- Delete removes file and returns true/false
- ListAll returns all saved intentions
- Path traversal attack rejected (relative path escaping root)
- Missing file returns null
- Unknown/malformed front matter surfaces parse error
