# Nested Memory Save

**Status:** Implemented (ancestor-only)
**Related tools:** `memory_save`, `memory_delete`, `memory_recall`
**Scope files:** `Eling.Core/Scope/ProjectScope.cs`, `Eling.Core/Memory/ScopedMemoryService.cs`, `Eling.Backend/Mcp/Tools/MemoryWriteTool.cs`

---

## Overview

Eling supports saving memories into **ancestor (nested) scopes** from a workspace that already has its own `.eling`. This lets a child project (e.g. a monorepo package) share memories upward into a parent project's memory tree without requiring separate initialization.

The mechanism is opt-in via the `project` parameter on `memory_save` (and symmetrically on `memory_delete`). It does **not** create new scopes — it only writes into scopes that already exist in the ancestor chain.

---

## Scope Chain Model

Eling resolves project scopes by walking **upward** from the current working directory, collecting every directory that contains an `.eling` folder. The result is an ordered chain, nearest-first:

```
C:\work\acme\acme-platform\integrations\payments\.eling   <- head (own scope)         [level 0]
C:\work\acme\acme-platform\integrations\.eling            <- ancestor                 [level 1]
C:\work\acme\acme-platform\.eling                         <- ancestor                 [level 2]
~/.config/eling/                                          <- global                   [terminal]
```

If the cwd has no `.eling` anywhere up to the user home, the workspace is **uninitialized** — default `scope=project` writes are blocked until the user consents to initialize.

### Chain discovery rules

- The walk stops at (and excludes) the user home directory. A `.eling` directly inside `~` is never treated as a project scope.
- Only directories **above** the cwd are considered — siblings are never discovered.
- The chain is built at runtime; no static registry is maintained.

---

## The `project` Parameter

### `memory_save`

```
memory_save(
  content = "...",
  scope   = "project",        // or "global"
  project = "integrations"    // logical name of an ancestor scope
)
```

| Condition | Result |
|---|---|
| Workspace has no `.eling` anywhere | Returns `init_required` signal — agent must ask user for consent to initialize |
| Workspace has own `.eling` + `project` matches an ancestor | Saves into that ancestor's `.eling/memories/` |
| Workspace has own `.eling` + `project` does **not** match any ancestor | Throws `InvalidProjectTargetException` with list of valid names |
| `project` specified with `scope=global` | Throws — contradictory arguments |

### `memory_delete`

Same rules apply. Deleting from an ancestor works the same way — the memory is removed from the target ancestor's store.

---

## What Is Allowed

### Writing into a nested scope

```
Cwd:          C:\work\acme\acme-platform\integrations\payments
Workspace:    Has .eling
Ancestor:     C:\work\acme\acme-platform\integrations\.eling (exists)

memory_save(project="integrations", content="...")
  -> saves into C:\work\acme\acme-platform\integrations\.eling\memories\
```

### Default write (no `project`)

```
memory_save(content="...")
  -> saves into C:\work\acme\acme-platform\integrations\payments\.eling\memories\  (head of chain)
```

### `scope=global`

```
memory_save(scope="global", content="...")
  -> saves into ~/.config/eling/memories/
```

---

## What Is NOT Allowed

### Sibling projects (not nested)

```
Cwd: C:\work\acme\acme-platform\integrations\payments
Target: "LogTail"   <- sibling, NOT an ancestor

memory_save(project="LogTail", content="...")
  -> throws InvalidProjectTargetException
  -> valid names: ["integrations", "acme-platform"]  (ancestors above payments)
```

The chain only walks **up**, never sideways. Sibling project names are not discoverable from a child workspace.

### Non-existent ancestor

```
Cwd: C:\work\acme\acme-platform\integrations\payments
Target: "SomeOtherProject"  <- no .eling exists here

memory_save(project="SomeOtherProject", content="...")
  -> throws InvalidProjectTargetException
```

The target must already have an `.eling` directory in the ancestor chain.

### Creating a new scope

`project` never triggers `.eling` creation. Only `memory_init_project` (after explicit user consent) can create a new scope.

---

## Implementation Notes

### Chain resolution

`ProjectScope.DiscoverChain(cwd)` returns `IReadOnlyList<ProjectScope>` ordered nearest-first. The first element is always the head for default writes.

### Ancestor name resolution

`ScopedMemoryService.ResolveAncestorProjectRoot(string projectName)`:
1. Skips the head level (`HasOwnScope ? 1 : 0`) — you cannot target your own scope via `project`.
2. Matches by the **last path segment** (`Path.GetFileName(root)`), case-insensitive.
3. Throws if the name is not found in the remaining chain levels.

### Write path

When `project` is provided, the call bypasses the head scope and writes directly into the matched ancestor's `IMemoryService` instance. The index for that ancestor is rebuilt afterward.

---

## Current Limitations

| Gap | Impact |
|---|---|
| **No sibling targeting** | Projects at the same level in the tree cannot share memories through `project`; only parent scopes are reachable |
| **No lateral registry** | There is no manifest of all known project scopes — discovery is purely upward |
| **Name matching** | `project="acme-platform"` and `project="Acme-Platform"` both match (case-insensitive `Path.GetFileName`) |

### Possible future enhancement

Adding a **project registry** (e.g. `.eling/projects.json` at the repo root listing all sibling workspaces and their `.eling` roots) would enable lateral targeting. This is tracked as a future enhancement, not implemented.

---

## See Also

- [Scope Chain Adoption Spec](../superpowers/specs/2026-09-09-scope-chain-adoption-design.md)
- [Memory Write Tool Source](../../src/backend/Eling.Backend/Mcp/Tools/MemoryWriteTool.cs)
- [Scoped Memory Service Source](../../src/backend/Eling.Core/Memory/ScopedMemoryService.cs)
