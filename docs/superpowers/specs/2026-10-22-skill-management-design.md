# Skill Management — Technical Design Spec

Date: 2026-10-22 (revised 2026-11-16)
Status: Revised draft for review
Scope: `Eling.Core` (Skill Domain, Storage, SQLite Index) · `Eling.Backend` (MCP Tools, REST Endpoints, DTOs) · `Eling.Dashboard` (Skill Viewer & Editor)

**Revision history**

- **2026-11-15** — Storage relocated from `~/.local/share/eling/skills/{ULID}/` to `~/.agents/skills/{name}/` (folders keyed by skill name; ULID lives only in frontmatter and index).
- **2026-11-15** — Trash relocated to `~/.agents/.trash/skills/{name}/` (self-contained catalog, sibling of `skills/`).
- **2026-11-15** - Skill index split into a dedicated `skill-index.db`, separate from the memories/snippets `index.db`.
- **2026-11-17** - `skill-index.db` relocated from `~/.local/share/eling/runtime/` to `~/.local/share/eling/`, as a sibling of `memory.db` (runtime is reserved for regenerable per-process state). No migration code: a fresh open creates the file lazily and the next rescan repopulates from the catalog (idempotent). A legacy file at the old path is ignored once new builds ship.
- **2026-11-15** — Naming cleanup: `memory_recall` → `recall`.
- **2026-11-15** — `skill_sync` dropped from v1 scope (YAGNI; skills consumed in place from the universal catalog).
- **2026-11-15** — `skill_adopt` dropped from v1 scope (YAGNI); later **rejected outright** on 2026-11-16 (see Resolved decisions).
- **2026-11-15** — Design made standalone: no third-party tool referenced; spec is self-contained.
- **2026-11-16** — Dashboard layout decided: card grid (`SkillCard`) + right-side detail/edit panel (`SkillDetailPanel`, master-detail split) replaces the drawer approach; skills page interaction confirmed via mock-up.
- **2026-11-16** — Smart Create documented as agent-side behavior (proposal-first skill creation when the user requests a reusable flow, no new backend surface); dashboard LLM draft-assist listed as a v2+ candidate.
- **2026-11-16** — Smart Create: unit-test example added (repo-specific workflow is a valid skill); skill-vs-memory distinction clarified — skills are repeatable multi-step workflows, memories are single rules/facts/decisions.
- **2026-11-16** — Adoption of external skills rejected outright (user decision): Eling manages only skills it creates; external skills remain owned by their creator's tooling, permanently.
- **2026-11-16** — §5 annotation: documented the implemented FTS sync (manual delete+insert inside one transaction, `porter unicode61 remove_diacritics 1`) in place of the original trigger wording.
- **2026-11-16** — Delete for external skills enabled (decision change): explicit user-confirmed move to trash, restorable, all other mutations forbidden; catalog records both origins deletable.
- **2026-11-16** — Adoption reinstated (user decision reversing the morning's rejection): `skill_adopt(name)` explicitly converts an `external`-origin skill to Eling-managed in place — assigns a ULID, writes Eling frontmatter (`id`, `createdAt`, `updatedAt`), preserves name/description/body/tags verbatim, flips `origin` to `eling`. Non-destructive; no content transformation. Refusal for unparseable frontmatter with a clear error (fix YAML first).

---

> All example paths use standard placeholder conventions
> (e.g. `~/.local/share/eling/skills/`). No personal machine paths or usernames
> are used, per the project hygiene rule.

---

## 1. Context & Problem Statement

Skills are reusable agent instruction packs: a directory containing a `SKILL.md` file (plus optional scripts/references). They are the primary mechanism by which an agent learns *how* to do a task rather than *what* to recall about a project.

Today skills live entirely outside Eling. They are stored as loose folders and moved around by hand or by external tooling. Eling has no knowledge of them: it cannot list, search, index, or recall them. Concretely:

1. **Discovery gap**: an agent working through `recall` on "how do I build a Blazor form" has no way to learn that a `collect-user-input` skill exists on this machine and where it lives. The agent either rediscovers the knowledge or silently misses it.
2. **No first-class artifact**: skills are the one durable agent-facing artifact Eling does not manage. Memories, snippets, codebase indexes, database connections, and tool policy all have canonical storage, a SQLite index, MCP tools, and a dashboard page. Skills have none of these.
3. **No first-class lifecycle**: skills are global by nature (one catalog for all projects), but today they have no Eling-level visibility, audit trail, or cross-reference to memories/snippets.

The universal agent skills directory `~/.agents/skills` already exists on this machine and holds the machine-wide skill catalog shared by agent targets (hundreds of skills today, supplied by assorted tooling and by hand). This directory is the de-facto universal catalog here.

Eling requires a **Skill Management** subsystem as a first-class citizen, scoped to **global/machine level only** (one catalog per machine, shared across all projects and worktrees). It must inherit Eling's proven storage/index architecture, store canonical `SKILL.md` files in the universal catalog location (`~/.agents/skills`), provide normalized relational indexing in SQLite, expose MCP tools, and integrate with `recall`. Agents that read the universal catalog consume Eling-managed skills in place — no distribution step. Eling **coexists** with other tools that write to the same tree under explicit ownership rules (Section 9): Eling fully manages the skills it creates; externally-supplied folders are read-only to Eling **except** an explicit user-called adoption (`skill_adopt`) that takes ownership in place.

---

## 2. Goals & Non-Goals

### Goals

1. **Universal-catalog storage**: canonical storage as individual skill directories under `~/.agents/skills/{name}/` (global scope only, folders keyed by skill name).
2. **Normalized relational indexing**: store skill metadata in SQLite (FTS5 for lexical search over name, description, and body) with dedicated child tables for tags, following the no-JSON-array-column rule used by memories and snippets.
3. **MCP tool surface**: `skill_save`, `skill_get`, `skill_search`, `skill_list`, `skill_delete`, `skill_restore`, `skill_adopt` — all global scope.
4. **Dashboard UI**: `/dashboard/skills` page for list, search, view, edit, delete, restore, and adopt.
5. **Recall integration**: `recall` (and a dedicated `skill_search`) can surface relevant skills by topic, returning name, description, and file path so the agent can load the full `SKILL.md`. Reads cover **both** Eling-created and externally-supplied skills.
6. **Consumed in place**: Eling-managed skills are written to the universal catalog that agents read directly — there is no copy-out/distribution step in v1. Agents with their own target directories (which do not read the universal catalog) are out of scope for v1.
7. **Cross-referencing (one-way)**: a skill can reference memories and snippets by id. Reverse references (memory/snippet → skill) are deferred to a later iteration to avoid invasive changes to already-shipped aggregates.
8. **Ownership model**: a single ownership marker (the `origin` column, Section 5) distinguishes Eling-managed skills (`origin = 'eling'`) from externally-supplied ones (`origin = 'external'`). Eling reads and indexes both; it mutates `eling`-origin folders, and may flip `external` → `eling` only through an explicit `skill_adopt` call by the user.

### Non-Goals (YAGNI)

- **No project or project-local scope.** Skills are global only, per the user's explicit decision. No `skill_copy_to_project`, no `skill_promote_to_global`, no per-repo `.eling/skills/`.
- **No skill execution sandbox, linter, or compiler** inside Eling.
- **No ownership of external tooling's capabilities.** Eling is self-contained: it reimplements no third-party functionality, manages no third-party config, and requires no third-party tool to operate.
- **No mutation of foreign folders, except adoption or a trash move.** Eling never writes, edits, or renames a folder under `~/.agents/skills` whose skill is `external`-origin — the only two exceptions are (a) an explicit user-confirmed delete to trash (restorable), and (b) an explicit `skill_adopt` call taking ownership in place (2026-11-16 amendment). Everything else stays forbidden.
- **No nested sub-directory hierarchy.** Each skill is one flat folder `{name}/` containing `SKILL.md` plus optional sibling files copied as-is.
- **No distribution/sync of any kind in v1** — not Eling-to-target, not remote/cloud. Skills are consumed in place from the catalog. A `skill_sync` for agents with their own target directories is a possible v2 feature (see Section 12).
- **No bi-directional cross-references in v1.** See goal #7.

---

## 3. Physical Storage & Canonical Format

### 3.1 Directory Layout

Global scope only, rooted at the universal agent skills catalog:

| Scope | Canonical Skill Files | Index / Cache | Trash |
| :--- | :--- | :--- | :--- |
| **Global** | `~/.agents/skills/{name}/SKILL.md` (+ siblings) | `~/.local/share/eling/skill-index.db` (dedicated; sibling of `memory.db`, kept separate from the memories/snippets index) | `~/.agents/.trash/skills/{name}/` |

The skill folder is the unit of storage. `SKILL.md` is mandatory; everything else in the folder is opaque payload that Eling copies but does not parse.

The folder name **is** the skill's validated kebab-case `name` (Section 3.2). There is no ULID folder layer; the ULID lives only inside the frontmatter (`id`) and in the index, and stays stable across folder renames (e.g. a skill rename).

**Relaxed external indexing of sync-flatten folders** (2026-11-16 amendment, this project): catalog tools may flatten a source path into a single directory whose name does not pass kebab-case validation (e.g. `_skills__skills__engineering__tdd/`). Such folders are still indexed, but only as `external`: the row's `name` is the `name` from the folder's frontmatter (must pass kebab validation), `body_path` keeps the real location (`skills/{folder}/SKILL.md`), and the display name is never the raw folder name. The row can never become Eling-managed, and its path resolution (list, get, delete-to-trash, adopt, restore) must always flow through the stored `body_path`, never through the display name. Folders that fail kebab validation *without* a valid frontmatter `name` are refused and reported. Scan order is deterministic (ordinal by folder name) so name claims are stable across rescans.

### 3.2 Canonical File Format

Each skill is stored as a folder `{name}` whose `SKILL.md` has a standardized YAML frontmatter header and a Markdown body (the skill instructions):

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

- `id` is a ULID. It is the stable identity of a skill Eling creates; it does not appear in the folder path.
- `name` is the human-facing skill name **and** the folder name. It is validated as lowercase kebab-case (`^[a-z0-9]+(-[a-z0-9]+)*$`, max 128 chars) and is unique across the catalog (enforced by the directory name and by the index's unique constraint).
- `description` is a one-line summary used for search ranking and recall (max 512 chars).
- `tags` are lowercase single words/phrases.
- `relatedMemories` / `relatedSnippets` use the same relation vocabulary as snippets (`implements`, `anti-pattern`, `example`, `test-case`, `reference`).
- `source` records optional upstream provenance (a URL or path the skill was imported from). Eling does not act on it; it is metadata.
- Skills scanned from the catalog that have **no** Eling-generated frontmatter (e.g. supplied by other tooling) are indexed as `external`; a ULID is assigned at index time and is **not** written back to the file. External files are never rewritten by Eling **except** through an explicit user-called `skill_adopt` (which adds `id`, `createdAt`, `updatedAt` frontmatter keys while preserving everything else verbatim).

### 3.3 Storage Rules

- One folder per skill, named by validated kebab-case skill name. Flat layout (no nested categories) — tags and search provide organization.
- `SKILL.md` is the only file Eling parses. Sibling files are stored and copied verbatim.
- Writes are atomic: write to a temp folder sibling, then rename. On failure, no partial skill remains.
- Renaming a skill = rename the folder (`{old}` → `{new}`) + update index; the ULID in frontmatter is preserved. External-origin skills are never renamed by Eling.
- Deletion (any origin, Eling-managed and externally-supplied) moves the folder to the catalog-local trash (`~/.agents/.trash/skills/{name}/`) before permanent removal, so an accidental `skill_delete` is recoverable via `skill_restore`. Externally-supplied content is never edited, renamed, or merged - only moved to trash. The index keeps a `trashed_at` marker; name uniqueness still applies while trashed (a trashed skill blocks reusing its name until restored or purged). Rebuilding the index preserves trashed rows verbatim - including their `trashed_at` markers and `body_path` - so a rebuilt index never un-blocks a name held by a trashed skill. The `.trash` directory sits under `~/.agents` alongside `skills/` so the whole skill catalog is self-contained in one place; other tooling syncs only `skills/` as the universal catalog, and Eling is the only writer in `.trash`.
- The index is rebuildable from the catalog: deleting `skill-index.db` loses no canonical data for `eling`-origin skills (their `SKILL.md` holds everything; `id` is in frontmatter). `external`-origin skills are re-discovered on the next scan and get fresh index ids.

---

## 4. Domain Model

In `Eling.Core`, a `Skill` aggregate (paralleling `Memory` and `Snippet`):

- `SkillId` (ULID value object)
- `Name` (kebab-case validated, max 128 chars; doubles as folder name)
- `Description` (max 512 chars)
- `Tags` (list of lowercase strings)
- `RelatedMemories` (list of `{ id, scope, relation }`)
- `RelatedSnippets` (list of `{ id, relation }`)
- `Source` (optional `{ upstream, tracked }`)
- `Origin` (`eling` | `external`) — ownership marker, stored in the index only; **not** in frontmatter
- `CreatedAt`, `UpdatedAt`
- `Body` (the Markdown instructions; stored in `SKILL.md` after frontmatter and mirrored in the DB for FTS)

Interfaces:

- `ISkillStorage` — canonical file read/write/delete/restore/rename/list for `~/.agents/skills` (and the trash).
- `ISkillIndex` — SQLite index operations (insert, update, delete, search, list, get).
- `ISkillService` - application-level orchestration (validation, ownership enforcement, cross-reference resolution, catalog scan, adopt). Cross-reference resolution validates and normalizes `relatedMemories`/`relatedSnippets` (scope and relation-vocabulary check, lowercase normalization, de-duplication); the referenced ids are opaque to Eling in v1 — their existence is not verified and the relations are never traversed or hydrated until bi-directional resolution lands (Section 12).
- `ISkillCatalogScanner` — walks `~/.agents/skills`, parses frontmatter where present, upserts index rows, marks missing folders as deleted. Runs on startup and on demand (dashboard "Rescan").

Ownership invariant: `origin = 'external'` rows are read-only to every mutating path, with two carve-outs (decision 2026-11-16): **delete → trash** (restorable, explicit user confirmation) and **adopt** (`skill_adopt`, flips `origin` to `eling`, preserving content verbatim). Edit/rename/overwrite outside those two paths remain forbidden.

---

## 5. SQLite Index Schema

Dedicated SQLite database `skill-index.db` (kept separate from the memories/snippets `index.db` so the skill subsystem owns its own schema and migration lifecycle), following the normalized (no JSON array columns) rule. The `body` column is denormalized into the `skills` table so the external-content FTS triggers can index it (mirroring the snippet spec's `code` column):

```sql
CREATE TABLE skills (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL UNIQUE,
    description TEXT NOT NULL,
    body TEXT NOT NULL,
    body_path TEXT NOT NULL,   -- relative path, e.g. skills/collect-user-input/SKILL.md under ~/.agents
    origin TEXT NOT NULL DEFAULT 'eling' CHECK (origin IN ('eling', 'external')),
    trashed_at TEXT NULL,
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

FTS sync is a manual delete + insert of the `skills_fts` rows inside the same transaction as the relational upsert (the SqliteMemoryIndex precedent); no triggers are registered. The tokenizer is `porter unicode61 remove_diacritics 1`. Trashed skills stay in the index with `trashed_at` set (excluded from search by default; `includeTrashed` lists them).

---

## 6. MCP Tool Surface

New tool group constant in `ToolCatalog.cs`:

```csharp
public const string SkillsGroup = "skills";
```

Tools (all global scope, no scope parameter needed):

| Tool | Purpose |
| :--- | :--- |
| `skill_save` | Create or update an **Eling-managed** skill from name + description + body (+ optional tags, related refs, sibling files). Writes `~/.agents/skills/{name}/`, updates index. Rejects kebab-case-invalid names and name collisions with `external`-origin skills. |
| `skill_get` | Retrieve a skill by id or name, returning frontmatter + body + file path + origin. |
| `skill_search` | FTS5 search over name/description/body with optional tag filter and origin filter; returns ranked snippets of matches. Covers both origins. |
| `skill_list` | Paginated list with optional tag/origin filter, sorted by name or updated_at. Optional `includeTrashed` flag. |
| `skill_delete` | Any origin: move skill folder to trash, mark index row trashed. External skills move to trash untouched — never edited, renamed, or merged — and are restorable. Edit/rename of external skills remains forbidden. |
| `skill_restore` | Restore a trashed skill folder to the active catalog and re-index. |
| `skill_adopt` | Convert an `external`-origin skill to Eling-managed **in place**: parse its frontmatter (refuse with a clear error if it doesn't parse — fix YAML first), assign a ULID (or reuse the index-assigned one), write Eling frontmatter keys (`id`, `createdAt`, `updatedAt`), preserve name/description/body/tags verbatim, flip `origin` to `eling`. Non-destructive; no other file is created or moved. |

Notes:

- No tool writes outside `~/.agents/skills` (any origin; only delete-to-trash for external), the trash directory, and Eling's data dir. There is no sync/distribution step in v1.
- Name uniqueness: `name` is the folder name and the natural key for `skill_get` by name; duplicate names are rejected on save (including collisions with `external`-origin skills).
- Ownership is enforced in `ISkillService`, not just in the tools: every mutating path goes through an `EnsureElingOwned(name)` guard.

---

## 7. REST Endpoints & Dashboard

Following the pattern of `/dashboard/snippets` and `/dashboard/memories`:

- `GET /api/skills` — list/search (query, tag, origin, page, pageSize, includeTrashed)
- `GET /api/skills/{id}` — get one
- `POST /api/skills` — create (Eling-managed)
- `PUT /api/skills/{id}` — update (Eling-managed only)
- `POST /api/skills/{id}/adopt` — adopt an `external`-origin skill (assign ULID, flip `origin` to `eling`, write Eling frontmatter keys; content preserved verbatim; refuses files whose frontmatter doesn't parse)
- `DELETE /api/skills/{id}` — delete to trash (any origin)
- `POST /api/skills/{id}/restore` — restore from trash
- `POST /api/skills/rescan` — rescan the catalog (`ISkillCatalogScanner`)

Dashboard page: `src/frontend/Eling.Dashboard/src/app/dashboard/skills/page.tsx`, following the pattern of `/dashboard/snippets` and `/dashboard/memories` (`page.tsx` → `Suspense` → client list component). Layout is a **master-detail split**, not a drawer:

- **Card grid (left, `SkillCard.tsx`)**: search box (by name/description/body), tag filter chips, origin filter (`all` / `eling` / `external`). Compact cards showing skill name, origin badge, one-line description, tag chips, and `updated` timestamp — mirroring `MemoryCard` styling (`rounded-xl border bg-card`, badge + secondary tag chips). Clicking a card selects it (highlighted border) and opens it in the right panel; clicking the selected card again (or ✕) deselects/closes the panel.
- **Detail panel (right, `SkillDetailPanel.tsx`)**: sticky side panel (≈45% width on desktop, stacked below the grid on narrow screens) showing the selected skill: name + origin badge + monospace file path, `id` and `updated`, an excerpt of the `SKILL.md` body rendered as preformatted text, and related memories/snippets (id + relation). Footer holds a Delete button (any origin; moves to trash) and an Edit toggle (Eling-managed only).
- **Edit mode (inline in the same panel)**: the Edit toggle swaps the body for a form — name, description, tags (comma-separated), and a `SKILL.md` body textarea — with Save/Cancel. Save calls `PUT /api/skills/{id}` and returns to view mode. Enabled only for `eling`-origin skills.
- **Read-only edit for `external` skills, delete and adopt allowed**: Edit (and name change) remain disabled for external skills with a hint that content is externally managed and Eling never writes it, except via explicit `skill_adopt`. Delete (to trash, restorable) is available for all origins; Adopt takes ownership in place (frontmatter keys added, content verbatim, `origin` → `eling`). The panel itself (view + related refs) is available for all skills.
- **Header actions**: skill count, "Rescan catalog" (`POST /api/skills/rescan`), and a "New Skill" button (opens the same panel in create mode).
- Trash view toggle (`includeTrashed`) surfaces trashed skills with a Restore action in place of Edit/Delete.

---

## 8. Recall Integration

Two integration points, mirroring how snippets are surfaced:

1. **`skill_search` MCP tool** — primary entry point for an agent that knows it wants skills. Covers both origins (this is the discovery win over any tool-scoped catalog).
2. **`recall` augmentation** — when recalling for a topic, also run an FTS search over skills (name + description + body) and append a compact "Relevant skills" block listing skill name, one-line description, origin, and file path. The agent can then `read` the full `SKILL.md`. Skills are *not* inlined into recall output (to avoid context inflation), only referenced by path — matching the snippet philosophy.

Tag: skills are excluded from the codebase indexer's walk (add `~/.agents/skills` handling alongside `.eling` and `~/.config/opencode`; note the catalog is the source of skill data and must not be double-indexed as plain code).

### Smart Create — the create half of the loop

The subsystem stores skills; it does not detect that a workflow deserves to become one. That detection is **agent behavior, not backend code**, and v1 documents it rather than implements it:

1. When a user asks for a flow with a reusable workflow (e.g. "commit": status check, staging discipline, commit lease, pre-commit hooks, lease release — or "run unit tests": per-csproj invocation, no solution-wide runs, isolated artifacts path vs the dev server, layer split, hook constraints), the agent may notice that no matching skill exists (`recall` / `skill_search` miss) and propose creating one — instead of rediscovering the workflow every session.
2. **Proposal first, never silent creation.** The agent drafts the `SKILL.md` (kebab-case name, one-line description, body) and asks the user to approve before calling `skill_save`. As with every Eling MCP write, creation is explicit and user-approved.
3. **Skills are workflows, memories are rules.** A skill is a repeatable multi-step procedure (per-repo is fine — e.g. a global catalog entry whose content covers one repository's unit-test workflow and pitfalls). A single convention, fact, decision, or preference ("run tests before commit", "never solution-wide") stays a memory. Scope of *content* does not restrict what may be a skill; scope of *storage* is always machine-global.
4. **No new backend surface.** Smart create rides entirely on `skill_save` + `skill_search`/`recall`; the heuristic lives in the agent's instructions, not in Eling's code.
5. **Explicitly out of scope for v1:** in-dashboard LLM drafting (the Skills page's "New Skill" remains a manual form), automatic skill creation without user confirmation, and any provider wiring inside the backend. A dashboard draft-assist ("describe the workflow → draft a `SKILL.md` for review → save") is a v2+ candidate if the agent-side loop proves insufficient.

---

## 9. Distribution & Coexistence

### 9.1 Distribution

There is no distribution step in v1. Eling-managed skills are consumed **in place** from the universal catalog (`~/.agents/skills`) by any agent configured to read it — which is how agents on this machine already see the skills there. Agents with their own target directories that do not read the universal catalog will not see Eling-managed skills; closing that gap is a possible v2 feature (`skill_sync` delivering catalog skills to configured target directories, manifest-gated), deliberately excluded from v1 as YAGNI.

### 9.2 Coexistence with other tools (the same tree)

`~/.agents/skills` may also be written by other tools that manage skills. Rules:

1. **Ownership is recorded in the Eling index (`origin`), not in a marker file.** A folder whose `name` is indexed as `eling` is Eling's; everything else is `external` and treated as externally-supplied.
2. **Eling never writes, renames, or edits `external` folders** — not in the catalog, and never copies them anywhere (no sync in v1). Allowed exceptions, per the 2026-11-16 amendments: (a) a delete to trash (restorable, explicit user confirmation); (b) an explicit `skill_adopt` call taking ownership in place — Eling writes only the new frontmatter keys (`id`, `createdAt`, `updatedAt`) into that file, preserving its content verbatim.
   - *Amendment (decision 2026-11-16, delete only → delete and adopt):* an external folder may be **moved to the trash** (`~/.agents/.trash/skills/{name}/`) by explicit user action — same semantics as eling-managed skills, restorable via restore, guarded by a confirmation UI naming the skill and its folder path. Content is never edited outside adoption; rename/save/overwrite stay forbidden; adoption is allowed only through `skill_adopt`.
3. **Tools that sync folders into the tree without pruning unknown entries leave Eling-managed folders intact.** A tool that removes or overwrites a folder Eling manages (e.g. installing or enabling a same-named skill) is outside Eling's control; on the next rescan Eling reports the folder as missing/changed and surfaces it in the dashboard rather than failing silently.
4. **Managing the same skill in two tools is unsupported.** The supported paths are: create/manage via Eling (`skill_save`), manage the skill via external tooling (Eling leaves it external and read-only), or take ownership via `skill_adopt` (Eling writes new frontmatter keys into the file, flips `origin` to `eling`). File surgery + rescan is unsupported; attempted out-of-band edits to frontmatter fall back to whatever remains in the file, at the user's risk.
5. **Recall and search are origin-agnostic** — discovery works for both, regardless of who manages the skill.
6. Capabilities of other skill tooling (installing tracked upstream repositories, audits, ignore rules, trash, hubs) are not reimplemented in Eling; Eling stands alone and requires none of them.

---

## 10. Migration / Catalog Scan

- `~/.agents/skills` is **not** moved, copied, or reset. Existing skills stay exactly where they are.
- On first run and on `POST /api/skills/rescan`, Eling scans the catalog: folders with Eling-style frontmatter are indexed as `eling`; everything else is indexed as `external` (assigning a fresh index id, never writing it back to the file). Index is rebuildable; rescanning is idempotent.
- External skills stay `external` until adopted; `skill_adopt` flips one in place to `eling` (atomically for the file, one transaction for the index). The scan is additive and non-destructive; Eling never modifies source files during rescan.
- Eling does not modify or delete anything outside `~/.agents/skills/{name}/`, the trash directory, and Eling's data dir.

---

## 11. Testing Strategy

- Unit tests in `Eling.Core.Tests`: `Skill` validation (kebab-case, uniqueness), `ISkillStorage` round-trip (write, rename, trash, restore), `ISkillIndex` FTS search ranking, tag/related-ref normalization, scanner upsert logic, ownership guard (`EnsureElingOwned`).
- Integration tests in `Eling.Backend.Tests`: MCP tool happy paths and error cases (duplicate name — including collision with an `external`-origin skill, missing skill, save → delete → restore cycle for both origins, external edit refused).
- Scanner tests: rescan indexes external folders without writing to them; missing folders marked; idempotency.
- Frontend: component tests for the skills page (list, search, origin filter, edit/read-only, delete, restore) following the existing dashboard test patterns.
- Manual verification: save a skill via MCP → it appears in `~/.agents/skills/{name}/SKILL.md`; confirm `recall` lists both origins by name + path.

---

## 12. Open Questions (for review)

Resolved in this revision:
- **Storage root** → `~/.agents/skills/{name}/` (universal catalog), folders keyed by kebab-case name.
- **Coexistence** → ownership via index `origin` column; Eling mutates only `eling` skills; externally-supplied folders stay externally-managed; recall/search cover both.
- **Trash location** → `~/.agents/.trash/skills/{name}/` (under `.agents`, alongside `skills/`; self-contained catalog). Revised 2026-11-15 after initial review.
- **Adoption** → **reinstated same day (2026-11-16)**: the morning's rejection is reversed by the user decision to shelf the "external = untouchable" premise for user-authored hand-made skills. `skill_adopt(name)` converts an external skill in place — see Revision history.
- **Distribution/sync** → dropped from v1 (2026-11-15). Skills are consumed in place from the universal catalog; there is no `skill_sync`, no sync targets, no manifest. A v2 `skill_sync` remains a candidate (see below).
- **Name changes** → rename folder atomically, ULID stable.

Still open:
1. **Sibling files**: how are extra files (scripts, references) supplied to `skill_save` over MCP? Recommendation: accept an optional list of `{ relativePath, contentBase64 }` entries.
2. **Trash retention**: how long before a trashed skill is permanently purged? Recommendation: manual purge only (no auto-purge), matching the "recoverable" promise; add a `skill_purge_trashed` tool only if needed later.
3. **Bi-directional cross-references**: deferred to v2. If wanted in v1, it requires extending the `Memory`/`Snippet` aggregates, their frontmatter serializers, their indexes, and recall hydration — roughly doubling scope. Flag if needed now.
4. **Adoption of external skills** → REINSTATED (2026-11-16, user decision): `skill_adopt` is in scope and implemented. Semantics: refuse files whose frontmatter doesn't parse (clear error, naming the parse failure); otherwise rewrite the frontmatter atomically — add `id`, `createdAt`, `updatedAt`, keep everything else verbatim; flip `origin` to `eling`; re-index. Non-destructive; the old adoption-rejection no longer governs.
5. **Rescan triggers**: startup + manual only, or also on a watcher/interval? Recommendation: startup + manual; no file watcher in v1.
6. **v2 candidate — `skill_sync` for non-universal agents**: if Eling-managed skills need to reach agents that do not read `~/.agents/skills` (e.g. agents with their own target directories), add a manifest-gated sync delivering catalog skills there. Excluded from v1 as YAGNI.

---

## 13. Success Criteria

- A skill saved via `skill_save` appears in `~/.agents/skills/{name}/SKILL.md` with valid kebab-case frontmatter.
- `skill_search` returns both Eling-managed and external skills by keyword in name, description, or body; origin is shown.
- `skill_delete` moves a skill of any origin to trash; `skill_restore` brings it back (any origin).
- No tool in v1 copies skills anywhere; an Eling-saved skill is immediately consumable in place from the catalog, and externally-supplied content in the same catalog is untouched.
- `recall` for a relevant topic lists skills by name + path + origin without inlining their bodies.
- The dashboard `/dashboard/skills` page lists, searches, edits, deletes, and restores skills without errors.
- Eling operates with no third-party tool installed; externally-supplied tooling in the same catalog is unaffected.
