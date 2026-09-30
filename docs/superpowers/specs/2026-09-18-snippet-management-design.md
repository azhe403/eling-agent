# Snippet Management & Bi-Directional Memory Referencing — Technical Design Spec

Date: 2026-09-18  
Status: Draft for review  
Scope: `Eling.Core` (Snippet Domain, Storage, SQLite Normalized Index), `Eling.Backend` (MCP Tools, REST Endpoints, DTOs), `Eling.Dashboard` (Snippet Viewer & Editor)

> All example paths and project names in this document use standard placeholder conventions
> (e.g. `C:\path\to\Eling`, `/path/to/Eling`, `~/.local/share/eling/snippets/`). No personal machine paths or usernames are used,
> per the project hygiene rule.

---

## 1. Context & Problem Statement

Currently, code snippets and reusable boilerplate templates are stored inside standard Eling memory entries (typically under `Note` or `Preference` types). This creates two structural problems:

1. **Context Window Inflation**: Memories carrying large multi-line code blocks significantly inflate the token footprint during `memory_recall` sessions, even when the agent only needs the cognitive rationale or design standard.
2. **Lack of Concrete Artifact Isolation**: There is no distinction between cognitive rules (the *why*, *lessons*, and *conventions*) and concrete code artifacts (the *how*, *syntax templates*, and *implementations*).

Eling requires a dedicated **Snippet Management** subsystem as a first-class citizen. It must inherit Eling's proven **Dual-Scope (Project + Global)** architecture, store canonical files in Markdown with YAML frontmatter, provide normalized relational indexing in SQLite without JSON array columns, and enable bi-directional cross-referencing between memories and snippets.

---

## 2. Goals & Non-Goals

### Goals
1. **Git-Native Storage**: Canonical storage as individual Markdown files in `.eling/snippets/` (Project Scope) and `~/.local/share/eling/snippets/` (Global Scope).
2. **Normalized Relational Indexing**: Store index metadata in SQLite without JSON array/string columns, using dedicated child tables with composite primary keys, foreign keys, and cascading deletes.
3. **Bi-Directional Memory Linking**: Snippets reference memories via semantic relation tags (`implements`, `anti-pattern`, `example`, `test-case`, `reference`), and memories can reference snippets.
4. **Lexical & Fuzzy Search**: Search snippets by keyword across title, description, and raw code using SQLite FTS5 (Porter stemmer + Unicode tokenizer) combined with indexed tag and language filters.
5. **Scope Parity with Memory**: Full support for `snippet_save`, `snippet_get`, `snippet_search`, `snippet_delete`, `snippet_promote_to_global`, `snippet_copy_to_project`, and `snippet_list`.
6. **Clean Code Extraction**: API and MCP endpoints provide clean, unescaped raw code extraction for effortless pasting and injection into target source trees.

### Non-Goals (YAGNI)
- No built-in code compilation, execution sandbox, or inline linter inside Eling.
- No remote cloud synchronization; distribution relies on Git for project scope and user-profile dotfiles for global scope.
- No nested sub-directory hierarchy inside `.eling/snippets/`; all snippets use flat ULID-based filenames.

---

## 3. Physical Storage & Canonical Format

### 3.1 Directory Layout

Paralleling the memory storage hierarchy:

| Scope | Canonical Snippet Files | Runtime Index / Cache |
| :--- | :--- | :--- |
| **Project Scope** | `.eling/snippets/{ULID}.md` | `.eling/runtime/index.db` *(git-ignored)* |
| **Global Scope** | `~/.local/share/eling/snippets/{ULID}.md` | `~/.local/share/eling/runtime/index.db` |

### 3.2 Canonical Markdown File Format

Each snippet is stored as a single `.md` file with a standardized YAML frontmatter header and a fenced code block:

```markdown
---
id: 01m2s5c9ypy6a0e1ht76rd8zv2
title: "JsonSnakeCaseHelper"
language: "csharp"
description: "System.Text.Json helper template enforcing snake_case naming with zero external dependencies"
tags:
  - csharp
  - dotnet
  - json
  - helper
relatedMemories:
  - id: "01m2s5a8sc8v6q2eeqneq3js6c"
    scope: "global"
    relation: "implements"
createdAt: "2026-09-18T02:26:25.8784706+00:00"
updatedAt: "2026-09-18T02:26:25.8784706+00:00"
---

```csharp
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Common.Json;

public static class JsonSnakeCaseHelper
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    public static string Serialize<T>(T data) => JsonSerializer.Serialize(data, Options);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
```
```

---

## 4. Domain Model (`Eling.Core`)

Located under `src/backend/Eling.Core/Snippet/`:

### 4.1 Value Objects & Enums

```csharp
namespace Eling.Core.Snippet;

public readonly record struct SnippetId(Ulid Value) : IComparable<SnippetId>
{
    public static SnippetId NewId() => new(Ulid.NewUlid());
    public static SnippetId Parse(string value) => new(Ulid.Parse(value));
    public static bool TryParse(string? value, out SnippetId result)
    {
        if (Ulid.TryParse(value, out var ulid))
        {
            result = new SnippetId(ulid);
            return true;
        }
        result = default;
        return false;
    }

    public int CompareTo(SnippetId other) => Value.CompareTo(other.Value);
    public override string ToString() => Value.ToString().ToLowerInvariant();
}

public enum SnippetRelationKind
{
    Implements,
    AntiPattern,
    Example,
    TestCase,
    Reference
}

public sealed record RelatedMemoryLink(
    MemoryId MemoryId,
    MemoryScopeKind Scope,
    SnippetRelationKind Relation = SnippetRelationKind.Implements);
```

### 4.2 Aggregate Root: `Snippet`

```csharp
namespace Eling.Core.Snippet;

public sealed class Snippet
{
    public required SnippetId Id { get; init; }
    public required string Title { get; set; }
    public required string Language { get; set; }
    public string? Description { get; set; }
    public required string Code { get; set; }
    public ImmutableArray<string> Tags { get; set; } = ImmutableArray<string>.Empty;
    public ImmutableArray<RelatedMemoryLink> RelatedMemories { get; set; } = ImmutableArray<RelatedMemoryLink>.Empty;
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; set; }
    public MemoryScopeKind Scope { get; set; } = MemoryScopeKind.Project;
    public string? ProjectName { get; set; }
    public string? ProjectRoot { get; set; }
}
```

---

## 5. Normalized Storage & Indexing Engine (SQLite)

In accordance with the Eling Database Architecture Rule, **no JSON array or JSON string columns** are used. All collections are normalized into dedicated relational tables with explicit Foreign Keys and cascading deletion.

### 5.1 SQLite Schema DDL

```sql
-- 1. Main Snippets Table
CREATE TABLE IF NOT EXISTS snippets (
    id TEXT PRIMARY KEY,
    title TEXT NOT NULL,
    language TEXT NOT NULL,
    description TEXT,
    code TEXT NOT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    file_hash TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_snippets_language ON snippets(language);
CREATE INDEX IF NOT EXISTS idx_snippets_updated_at ON snippets(updated_at DESC);

-- 2. Normalized Tags Table (Many-to-Many / 1:N per snippet)
CREATE TABLE IF NOT EXISTS snippet_tags (
    snippet_id TEXT NOT NULL,
    tag TEXT NOT NULL,
    PRIMARY KEY (snippet_id, tag),
    FOREIGN KEY (snippet_id) REFERENCES snippets(id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_snippet_tags_tag ON snippet_tags(tag);

-- 3. Normalized Memory Relations Table
CREATE TABLE IF NOT EXISTS snippet_memory_links (
    snippet_id TEXT NOT NULL,
    memory_id TEXT NOT NULL,
    memory_scope TEXT NOT NULL, -- 'project' | 'global'
    relation TEXT NOT NULL,     -- 'implements' | 'anti-pattern' | 'example' | 'test-case' | 'reference'
    PRIMARY KEY (snippet_id, memory_id),
    FOREIGN KEY (snippet_id) REFERENCES snippets(id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_snippet_memory_links_memory ON snippet_memory_links(memory_id);

-- 4. Full-Text Search Virtual Table (FTS5)
CREATE VIRTUAL TABLE IF NOT EXISTS snippets_fts USING fts5(
    id UNINDEXED,
    title,
    description,
    code,
    tokenize = 'porter unicode61 remove_diacritics 1'
);
```

### 5.2 Storage Layer Interfaces

```csharp
namespace Eling.Core.Snippet.Storage;

public interface ISnippetRepository
{
    Task<Snippet?> GetByIdAsync(SnippetId id, CancellationToken ct = default);
    Task SaveAsync(Snippet snippet, CancellationToken ct = default);
    Task<bool> DeleteAsync(SnippetId id, CancellationToken ct = default);
    Task<IReadOnlyList<Snippet>> ListAllAsync(CancellationToken ct = default);
}

public interface ISnippetIndex
{
    Task IndexAsync(Snippet snippet, string fileHash, CancellationToken ct = default);
    Task RemoveAsync(SnippetId id, CancellationToken ct = default);
    Task<IReadOnlyList<SnippetSearchResult>> SearchAsync(SnippetSearchQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<Snippet>> GetByMemoryIdAsync(MemoryId memoryId, CancellationToken ct = default);
}
```

---

## 6. MCP Tool Suite (`Eling.Backend`)

Located under `src/backend/Eling.Backend/Mcp/Tools/`:

| Tool Name | Key Parameters | Description |
| :--- | :--- | :--- |
| `snippet_save` | `title`, `language`, `code`, `description?`, `tags?`, `relatedMemories?`, `scope?`, `project?` | Saves or updates a snippet in project or global scope. |
| `snippet_get` | `id`, `scope?`, `project?` | Retrieves full snippet details including raw code payload and linked memories. |
| `snippet_search` | `query`, `language?`, `tags?`, `memoryId?`, `scope?`, `limit?` | Fast FTS + relational lookup across project and global snippets. |
| `snippet_delete` | `id`, `scope?`, `project?` | Permanently deletes a snippet file and purges its relational index. |
| `snippet_promote_to_global` | `id`, `operation: copy \| move` | Promotes a project-scoped snippet to global scope. |
| `snippet_copy_to_project` | `id`, `operation: copy \| move`, `project?` | Copies a global snippet into the target project scope. |
| `snippet_list` | `scope?`, `language?`, `tag?`, `limit?` | Lists recent snippets sorted by update time. |

---

## 7. Bi-Directional Memory Interaction

1. **Snippet $\rightarrow$ Memory**: When inspecting a snippet, callers get an array of linked `RelatedMemoryLink` items, revealing why the snippet exists and what rules govern its use.
2. **Memory $\rightarrow$ Snippet**: During `memory_recall` or `memory_get`, Eling queries `snippet_memory_links` to hydrate lightweight snippet reference descriptors (e.g. `{ snippetId: "...", title: "...", language: "csharp", relation: "implements" }`) without embedding raw code in the memory text.
3. **On-Demand Hydration**: When the agent requires the code to apply in the workspace, it calls `snippet_get(id)` to retrieve the exact code block.

---

## 8. Dashboard UI/UX Specification

1. **Sidebar Navigation**: Add a dedicated **Snippets** tab alongside *Memories* and *Intentions*.
2. **Snippet Cards & Table View**: Display snippet title, language badge, tags, and related memory badges.
3. **Code Viewer & Editor**: Syntax-highlighted code viewer with one-click "Copy Code" button, raw download option, and language selector.
4. **Interactive Memory Bridge**: Clicking a linked memory badge inside a snippet opens the memory inspector drawer, and vice versa.

---

## 9. Implementation Phases

- **Phase 1 (Core Domain & Storage)**: Implement `SnippetId`, `Snippet`, Markdown file parser/serializer, and `SqliteSnippetIndex` with normalized tables.
- **Phase 2 (Backend Services & MCP Tools)**: Register `SnippetService`, MCP tools (`snippet_*`), and REST API endpoints.
- **Phase 3 (Memory Bi-directional Integration)**: Link `memory_recall` and `IMemoryIndex` with snippet reference lookups.
- **Phase 4 (Dashboard UI)**: Implement Snippet view, Monaco editor/code viewer, and memory navigation cards in `Eling.Dashboard`.
