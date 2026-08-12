# Phase 1 Step 3: Canonical Markdown Storage Design

## Goal
Implement Markdown-based persistent storage for Eling memories in `Eling.Storage` and integration tests in `Eling.Storage.Tests`.

## Dependencies
- Add `YamlDotNet` NuGet package to `Eling.Storage`.

## Storage API Contracts (`Eling.Storage`)

### `IMemoryStorage` Interface
```csharp
using Eling.Core;

namespace Eling.Storage;

public interface IMemoryStorage
{
    Task SaveAsync(Memory memory);
    Task<Memory?> GetByIdAsync(MemoryId id);
    Task<bool> DeleteAsync(MemoryId id);
    Task<IReadOnlyCollection<Memory>> ListAllAsync();
}
```

### `FileSystemMemoryStorage` Class
Implements `IMemoryStorage`.
- Constructor: `FileSystemMemoryStorage(string rootPath)` (defaults to base directory).
- Safe path resolution helper: Computes `Path.Combine(rootPath, "memories", $"{id.Value}.md")`.
- Path traversal check: Throws `ArgumentException` if the resulting path resolved outside `Path.Combine(rootPath, "memories")`.

### Serialization Format
Files saved as:
```markdown
---
id: <Id>
type: <Type>
status: <Status>
tags:
  - <Tag1>
  - <Tag2>
created_at: <ISO 8601>
updated_at: <ISO 8601>
source: <Source>
---
<Content>
```

YAML Front matter maps:
- `id` -> `Memory.Id.Value`
- `type` -> `Memory.Type` (lowercase string)
- `status` -> `Memory.Status` (lowercase string)
- `tags` -> `Memory.Tags`
- `created_at` -> `Memory.CreatedAt` (ISO 8601 offset format)
- `updated_at` -> `Memory.UpdatedAt` (ISO 8601 offset format)
- `source` -> `Memory.Source` (null omitted or null output)

File body maps to `Memory.Content`.

## Tests (`tests/Eling.Storage.Tests/FileSystemMemoryStorageTests.cs`)
Integration tests covering:
- Save creates directories and writes Markdown file.
- Get reads correctly.
- Missing memory returns `null`.
- Delete removes file.
- Malformed YAML throws parse exception.
- Directory traversal attempts are rejected.
