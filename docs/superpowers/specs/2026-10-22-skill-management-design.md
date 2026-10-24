# Skill Management — Technical Design Spec

Date: 2026-10-22  
Status: Draft for review  
Scope: `Eling.Core` (Skill Domain, Storage, SQLite Index), `Eling.Backend` (MCP Tools, REST Endpoints, DTOs), `Eling.Dashboard` (Skill Viewer & Editor)

> All example paths use standard placeholder conventions
> (e.g. `~/.local/share/eling/skills/`). No personal machine paths or usernames
> are used, per the project hygiene rule.

---

## 1. Context & Problem Statement

Skills are reusable agent instruction packs: a directory containing a `SKILL.md` file (plus optional scripts/references). They are the primary mechanism by which an agent learns *how* to do a task rather than *what* to recall about a project.

Today skills live entirely outside Eling. They are stored as loose folders (e.g. `~/.config/opencode/skills/`, `~/.config/skillshare/skills/`) and moved around by hand or by external CLIs. Eling has no knowledge of them: it cannot list, search, index, or recall them. Concretely:

1. **Discovery gap**: an agent working through `memory_recall` on "how do I build a Blazor form" has no way to learn that a `collect-user-input` skill exists on this machine and where it lives. The agent either rediscovers the knowledge or silently misses it.
2. **No first-class artifact**: skills are the one durable agent-facing artifact Eling does not manage. Memories, snippets, codebase indexes, database connections, and tool policy all have canonical storage, a SQLite index, MCP tools, and a dashboard page. Skills have none of these.
3. **No first-class lifecycle**: skills are global by nature (one catalog for all projects), but today they have no Eling-level visibility, audit trail, cross-reference to memories/snippets, or sync to agent targets.

Eling requires a **Skill Management** subsystem as a first-class citizen, scoped to **global/machine level only** (one catalog per machine, shared across all projects and worktrees). It must inherit Eling's proven storage/index architecture, store canonical files as Markdown with YAML frontmatter, provide normalized relational indexing in SQLite, expose MCP tools, integrate with `memory_recall`, and **distribute to agent target directories itself** (no external distributor dependency).

---

## 2. Goals & Non-Goals

### Goals

1. **Git-native storage**: canonical storage as individual skill directories under `~/.local/share/eling/skills/{ULID}/SKILL.md` (global scope only).
2. **Normalized relational indexing**: store skill metadata in SQLite (FTS5 for lexical search over name, description, and body) with dedicated child tables for tags, following the no-JSON-array-column rule used by memories and snippets.
3. **MCP tool surface**: `skill_save`, `skill_get`, `skill_search`, `skill_list`, `skill_delete`, `skill_restore`, `skill_sync` — all global scope.
4. **Dashboard UI**: `/dashboard/skills` page for list, search, view, edit, delete, restore, and sync.
5. **Recall integration**: `memory_recall` (and a dedicated `skill_search`) can surface relevant skills by topic, returning name, description, and file path so the agent can load the full `SKILL.md`.
6. **Self-contained sync**: Eling copies skill folders from its global store to configured agent target directories (e.g. `~/.config/opencode/skills/`) via `skill_sync`. No external distributor (skillshare or otherwise) is required; Eling owns storage, indexing, and distribution end to end. External tools remain usable but are not dependencies.
7. **Cross-referencing (one-way)**: a skill can reference memories and snippets by id. Reverse references (memory/snippet → skill) are deferred to a later iteration to avoid invasive changes to already-shipped aggregates.

### Non-Goals (YAGNI)

- **No project or project-local scope.** Skills are global only, per the user's explicit decision. No `skill_copy_to_project`, no `skill_promote_to_global`, no per-repo `.eling/skills/`.
- **No skill execution sandbox, linter, or compiler** inside Eling.
- **No dependency on any external distributor.** Eling does not shell out to skillshare or any other CLI for sync, audit, or trash. Those concerns are Eling's own.
- **No nested sub-directory hierarchy beyond the ULID folder.** Each skill is one folder `{ULID}/` containing `SKILL.md` plus optional sibling files copied as-is.
- **No remote/cloud sync.** Distribution is local, to configured agent target directories.
- **No bi-directional cross-references in v1.** See goal #7.

---

## 3. Physical Storage & Canonical Format

### 3.1 Directory Layout

Paralleling the memory and snippet storage hierarchy, but global scope only:

| Scope | Canonical Skill Files | Runtime Index / Cache |
| :--- | :--- | :--- |
| **Global** | `~/.local/share/eling/skills/{ULID}/SKILL.md` (+ siblings) | `~/.local/share/eling/runtime/index.db` (shared with memories/snippets) |

The skill folder is the unit of storage. `SKILL.md` is mandatory; everything else in the folder is opaque payload that Eling copies but does not parse.

### 3.2 Canonical File Format

Each skill is stored as a folder whose `SKILL.md` has a standardized YAML frontmatter header and a Markdown body (the skill instructions):

```markdown
---
id: 01m6k2a9ypy6a0e1ht76rd8zv2
name: "collect-user-input"
description: "Build forms, validate data, and react to user input in Blazor."
tags:
  - blazor
  - forms
  - validation
relatedMemories:
  - id: "01m2s5a8sc8v6q2eeqneq3js6c"
    scope: "global"
    relation: "implements"
relatedSnippets:
  - id: "01m3x9c1ypy6a0e1ht76rd8zv2"
    relation: "example"
source:
  upstream: ""
  tracked: false
createdAt: "2026-10-22T10:00:00.0000000+00:00"
updatedAt: "2026-10-22T10:00:00.0000000+00:00"
---

# collect-user-input

...skill instructions...
```

- `id` is a ULID, used as the folder name.
- `name` is the human-facing skill name.
- `description` is a one-line summary used for search ranking and recall.
- `tags` are lowercase single words/phrases.
- `relatedMemories` / `relatedSnippets` use the same relation vocabulary as snippets (`implements`, `anti-pattern`, `example`, `test-case`, `reference`).
- `source` records optional upstream provenance (a URL or path the skill was imported from). Eling does not act on it; it is metadata.

### 3.3 Storage Rules

- One folder per skill, named by ULID. Flat layout (no nested categories) — tags and search provide organization.
- `SKILL.md` is the only file Eling parses. Sibling files are stored and copied verbatim.
- Writes are atomic: write to a temp folder, then rename. On failure, no partial skill remains.
- Deletion moves the folder to Eling's trash (`~/.local/share/eling/trash/skills/{ULID}/`) before permanent removal, so an accidental `skill_delete` is recoverable via `skill_restore`.

---

## 4. Domain Model

In `Eling.Core`, a `Skill` aggregate (paralleling `Memory` and `Snippet`):

- `SkillId` (ULID value object)
- `Name` (non-empty, max 128 chars)
- `Description` (max 512 chars)
- `Tags` (list of lowercase strings)
- `RelatedMemories` (list of `{ id, scope, relation }`)
- `RelatedSnippets` (list of `{ id, relation }`)
- `Source` (optional `{ upstream, tracked }`)
- `CreatedAt`, `UpdatedAt`
- `Body` (the Markdown instructions; stored in `SKILL.md` after frontmatter and mirrored in the DB for FTS)

Interfaces:

- `ISkillStorage` — canonical file read/write/delete/restore/list for the global store.
- `ISkillIndex` — SQLite index operations (insert, update, delete, search, list, get).
- `ISkillService` — application-level orchestration (validation, cross-reference resolution, sync).

---

## 5. SQLite Index Schema

New tables in the shared runtime `index.db`, following the normalized (no JSON array columns) rule. The `body` column is denormalized into the `skills` table so the external-content FTS triggers can index it (mirroring the snippet spec's `code` column):

```sql
CREATE TABLE skills (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    description TEXT NOT NULL,
    body TEXT NOT NULL,
    body_path TEXT NOT NULL,   -- relative path to SKILL.md
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE TABLE skill_tags (
    skill_id TEXT NOT NULL REFERENCES skills(id) ON DELETE CASCADE,
    tag TEXT NOT NULL,
    PRIMARY KEY (skill_id, tag)
);

CREATE TABLE skill_related_memories (
    skill_id TEXT NOT NULL REFERENCES skills(id) ON DELETE CASCADE,
    memory_id TEXT NOT NULL,
    memory_scope TEXT NOT NULL,
    relation TEXT NOT NULL,
    PRIMARY KEY (skill_id, memory_id, memory_scope)
);

CREATE TABLE skill_related_snippets (
    skill_id TEXT NOT NULL REFERENCES skills(id) ON DELETE CASCADE,
    snippet_id TEXT NOT NULL,
    relation TEXT NOT NULL,
    PRIMARY KEY (skill_id, snippet_id)
);

CREATE VIRTUAL TABLE skills_fts USING fts5(
    name, description, body,
    content='skills', content_rowid='rowid',
    tokenize='porter unicode61'
);
```

Triggers keep `skills_fts` in sync with `skills` (insert/update/delete), mirroring the memory index pattern.

---

## 6. MCP Tool Surface

New tool group constant in `ToolCatalog.cs`:

```csharp
public const string SkillGroup = "skill";
```

Tools (all global scope, no scope parameter needed):

| Tool | Purpose |
| :--- | :--- |
| `skill_save` | Create or update a skill from name + description + body (+ optional tags, related refs, sibling files). Writes canonical folder, updates index. |
| `skill_get` | Retrieve a skill by id or name, returning frontmatter + body + file path. |
| `skill_search` | FTS5 search over name/description/body with optional tag filter; returns ranked snippets of matches. |
| `skill_list` | Paginated list with optional tag filter, sorted by name or updated_at. Optional `includeTrashed` flag. |
| `skill_delete` | Move skill folder to trash, remove from index. |
| `skill_restore` | Restore a trashed skill folder to the active store and re-index. |
| `skill_sync` | Copy skill folders from Eling's global store to configured agent target directories. Returns per-target results (copied/skipped/failed). Does not write canonical files. |

Notes:

- `skill_save` never touches agent target directories. Only `skill_sync` does.
- `skill_sync` reads its target list from Eling's config (see Section 9). For each target, it copies the skill folder (or removes it if the skill was deleted) and reports results. No external CLI is invoked.
- Name uniqueness: `name` is treated as the natural key for `skill_get` by name; duplicate names are rejected on save.

---

## 7. REST Endpoints & Dashboard

Following the pattern of `/dashboard/snippets` and `/dashboard/memories`:

- `GET /api/skills` — list/search (query, tag, page, pageSize, includeTrashed)
- `GET /api/skills/{id}` — get one
- `POST /api/skills` — create
- `PUT /api/skills/{id}` — update
- `DELETE /api/skills/{id}` — delete (to trash)
- `POST /api/skills/{id}/restore` — restore from trash
- `POST /api/skills/sync` — trigger `skill_sync` (global) or per-skill sync

Dashboard page: `src/frontend/Eling.Dashboard/src/app/dashboard/skills/page.tsx`

- List view with search box and tag filter chips.
- Detail drawer showing frontmatter, body (rendered Markdown), file path, and related memories/snippets.
- Edit form (name, description, tags, body textarea, sibling files).
- Trash view toggle (`includeTrashed`).
- "Sync to agents" button (global + per-skill).
- Link to open the skill folder in the file explorer.

---

## 8. Recall Integration

Two integration points, mirroring how snippets are surfaced:

1. **`skill_search` MCP tool** — primary entry point for an agent that knows it wants skills.
2. **`memory_recall` augmentation** — when recalling for a topic, also run an FTS search over skills (name + description + body) and append a compact "Relevant skills" block listing skill name, one-line description, and file path. The agent can then `read` the full `SKILL.md`. Skills are *not* inlined into recall output (to avoid context inflation), only referenced by path — matching the snippet philosophy.

Tag: skills are excluded from the codebase indexer's walk (add `~/.local/share/eling/skills` to the default exclude roots, alongside `.eling` and `~/.config/opencode`).

---

## 9. Agent Target Sync

Eling distributes skills to agent target directories itself. No external distributor is required.

### 9.1 Configuration

Sync targets are configured in Eling's config (e.g. `~/.local/share/eling/config.json` or the existing settings store), as a list of target directories:

```json
{
  "skillSyncTargets": [
    { "name": "opencode", "path": "~/.config/opencode/skills", "enabled": true },
    { "name": "claude-code", "path": "~/.config/claude/skills", "enabled": false }
  ]
}
```

- Paths support `~` expansion.
- Targets are opt-in (`enabled: false` by default for anything not explicitly configured).
- The dashboard provides a UI to add/remove/enable targets.

### 9.2 Sync Semantics

For each enabled target, `skill_sync` (global) or per-skill sync:

1. For each active skill in Eling's store: copy the skill folder to `{target}/{skill.name}/` (overwrite).
2. For each folder in `{target}/` that no longer corresponds to an active skill: remove it (only if Eling previously synced it — tracked via a small manifest `{target}/.eling-sync-manifest.json` to avoid deleting user-managed skills).
3. Report per-target: `{ copied, updated, removed, skipped, failed }`.

### 9.3 Safety

- Eling never deletes a target folder it did not create/sync (manifest-gated).
- Sync is explicit (button or `skill_sync` call), never automatic on save, to avoid surprising filesystem side effects.
- If a target path does not exist, Eling creates it (with confirmation in the dashboard).

---

## 10. Migration / Adoption

- Existing skills in any folder (e.g. `~/.config/opencode/skills/`, `~/.config/skillshare/skills/`) are **not** moved automatically.
- The dashboard offers an "Import from folder" action: pick a source directory, Eling scans for subfolders containing `SKILL.md`, normalizes each (generate ULID, derive `name`/`description` from frontmatter or folder name, default tags/relations to empty, record `source.upstream` as the source path, copy sibling files, index immediately), and reports results.
- Eling does not modify or delete the source directory. Import is additive and non-destructive.
- Normalization (per builder check): full normalization on import — not a shallow byte copy — so imported skills are immediately searchable and editable.

---

## 11. Testing Strategy

- Unit tests in `Eling.Core.Tests`: `Skill` validation, `ISkillStorage` round-trip, `ISkillIndex` FTS search ranking, tag/related-ref normalization, sync manifest logic.
- Integration tests in `Eling.Backend.Tests`: MCP tool happy paths and error cases (duplicate name, missing skill, restore, sync to a temp target dir).
- Frontend: component tests for the skills page (list, search, edit, delete, restore, sync) following the existing dashboard test patterns.
- Manual verification: save a skill via MCP, run `skill_sync` to a temp target, confirm the folder appears there with `SKILL.md`.

---

## 12. Open Questions (for review)

1. **Sync trigger**: should `skill_save` auto-call `skill_sync`, or require an explicit `skill_sync`? Recommendation: explicit, to avoid surprising filesystem side effects during a save; the dashboard's "Sync" button makes it one click.
2. **Sibling files**: how are extra files (scripts, references) supplied to `skill_save` over MCP? Recommendation: accept an optional list of `{ relativePath, contentBase64 }` entries.
3. **Trash retention**: how long before a trashed skill is permanently purged? Recommendation: manual purge only (no auto-purge), matching the "recoverable" promise; add a `skill_purge_trashed` tool only if needed later.
4. **Bi-directional cross-references**: deferred to v2. If wanted in v1, it requires extending the `Memory`/`Snippet` aggregates, their frontmatter serializers, their indexes, and recall hydration — roughly doubling scope. Flag if needed now.

---

## 13. Success Criteria

- A skill saved via `skill_save` appears in `~/.local/share/eling/skills/{ULID}/SKILL.md` with valid frontmatter.
- `skill_search` returns it by keyword in name, description, or body.
- `skill_sync` copies it to a configured target directory, tracked by a manifest.
- `skill_delete` moves it to trash; `skill_restore` brings it back.
- `memory_recall` for a relevant topic lists the skill by name + path without inlining its body.
- The dashboard `/dashboard/skills` page lists, searches, edits, deletes, restores, and syncs skills without errors.
- No external distributor is required for any of the above.