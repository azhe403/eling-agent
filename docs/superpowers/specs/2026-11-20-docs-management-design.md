# Docs Management Subsystem (Spec, Plan, Scratch) Design Specification

- **Date:** 2026-11-20
- **Status:** Proposed (awaiting user review)

> Paths use placeholders. `DATA/eling` = `~/.local/share/eling`. `CONFIG/eling` = `~/.config/eling`. No machine paths in this document.

---

## 1. Overview & Scope

Eling manages memories, intentions, and skills. A memory is a **small note**: one fact, written
to be recalled and replaced. A document is a different thing — a **structured artefact** whose
natural forms are the spec and the plan. This spec adds docs as a fourth subsystem with its own
domain, storage, index, and tools.

The distinction that drives the design is shape and role, not byte size:

| | Memory | Docs |
|---|---|---|
| Shape | small note, single block | document with sections |
| Kinds | fact / preference / decision / lesson / note | **spec / plan / doc / scratch** |
| Unit of replacement | the whole note (supersede) | an individual section can go stale |
| Lifecycle | active / superseded / archived | **spec: draft→review→approved→implemented; plan: per-step progress** |
| Code references | rare | **essential** — a spec names files, classes, tools |
| Audience | recalled by the agent | **read by a human, executed by an agent** |
| Retrieval | whole note | per section |

Two consequences follow. A **plan carries execution state** — `- [ ]` / `- [x]` checkboxes are
progress, parsed at indexing time, never rewritten. A **spec carries approval state and code
targets** — it moves through review, and it names symbols that must still exist when it is
implemented. Folding either into a small note would make both models wrong, which is why docs is
a separate subsystem rather than `MemoryKind.Doc`.

Today `docs/` holds 153 Markdown files totalling 2.1 MB — 83 specs, 58 plans, 2 standards, plus
architecture and how-to pages. The pain is already visible in the corpus:

- **No index.** Finding a document requires already knowing its filename.
- **Supersession is free text.** Specs carry hand-written headers such as
  `> **Superseded by `memory_recall` rename.** ... Do not implement against this spec.`
  A human wrote that because the need existed; it is unqueryable, so no tool can honour it.
- **Whole-file retrieval.** A 34 KB spec is either fully injected into context or not at all.
- **Plan progress is invisible.** Whether a plan is half-executed lives in checkboxes nobody
  aggregates.
- **Staleness is invisible.** A spec can reference `MemoryService` or `docs/foo.md#bar` long
  after either has moved or been deleted, and nothing notices.
- **No reading path.** Specs and plans sit in flat folders with no topic grouping and no order.

### 1.1 In scope (this phase)

**Domain (`Eling.Core/Docs/`)**

- `DocId`, `DocOwnership` (`managed | external`), `DocOrigin` (`current | ancestor | shard | global`), `DocKind` (`spec | plan | doc | scratch`), `DocStatus` per kind
- `Doc`, `DocSection`, `DocRef`, `DocRelation` entities
- Optional front-matter parser for managed documents — `kind`, `topic`, `order`, `status`, `supersededBy`, `spec`, `sections{}`; malformed front-matter is non-fatal and recorded as a finding
- Metadata inference for external documents — `kind`, `topic`, `status`, `read_order` derived from folder, first heading, and filename; no front-matter is ever written to them
- Heading chunker: ATX headings, `_preamble` section, fenced code never splits, GitHub-flavoured anchors with `-1`/`-2` dedupe
- Plan checkbox counter: `- [ ]` / `- [x]` per section and per document, fenced code excluded
- `DocScopeResolver` — resolves `ownership` and `origin`; reuses `ResolveCanonicalProjectRoot` and `ResolveProjectLocalDir`; no duplicated path logic

**Storage and index**

- `docs` table — ownership, origin, rel_path, kind, title, status, topic, order, superseded_by, steps_total, steps_done, file_hash, mtime
- `doc_sections` table — anchor, heading, level, line range, content_hash, status, annotation, per-section step counts
- `doc_refs` table — ref_kind (symbol | path | anchor_link), ref_value, resolved, resolved_at
- `doc_relations` table — implements | supersedes | references
- `doc_audit` table — action, actor, before_hash, after_hash, at
- `sections_fts` (porter unicode61, remove_diacritics 1) and `sections_fts_trigram` (trigram), both mandatory
- Per-tier index placement: `DATA/eling/projects/<name>-<hash>/docs.db`, `<repo>/.eling/docs-index.db`, `DATA/eling/docs.db`
- Incremental scan by `file_hash`; rebuild-from-empty after index deletion

**Scope policy**

- Coverage: `default` = `docs/**` + `.eling/docs/**`; `include: []`; `exclude: []`
- Project-scope `decision`: `ask | disabled | clear` — governs `<repo>/.eling/docs/` **only**
- `disabled` leaves zero repo footprint: no folder, no `docs-index.db`, no sidecar, no stray file under `<repo>/.eling/`; writes route to the local shard
- `disabled` does **not** stop `docs/**` from being indexed — `docs/` predates Eling and is governed by coverage patterns alone

**MCP tools (11)**

- `doc_search` — section-level FTS, filters `ownership`, `origin`, `kind`, `topic`, `status`, `pathPrefix`
- `doc_read` — one section, whole document, or `mode: 'map'` heading tree
- `doc_list` — grouped by topic, ordered by `read_order`, with ownership, origin, kind, and plan progress
- `doc_save` — create/update a managed document with automatic front-matter; `promote: true` writes into `docs/` and makes it external; refuses in-place edits of external documents
- `doc_annotate` — per-section status and annotation pointer on managed documents; refused for external
- `doc_staleness` — unresolved refs, superseded-without-replacement, stale drafts, untouched documents; covers external documents too
- `doc_audit` — audit entries per managed document or scope; always empty for external
- `doc_index` — incremental rescan within the allowed scope
- `doc_relation` — read spec↔plan edges in both directions; write them only between managed documents
- `doc_scope_policy` — inspect/change coverage and decision; adding an include requires approval
- `doc_offer_managed` — read, raise, or answer the managed-docs recommendation; remembers `accepted` / `declined` / `never` per project with a timestamp and a spec counter

**Offer policy**

- Third independent control (§3.14), separate from coverage and from `decision`
- States `unasked` / `accepted` / `declined` / `never`; remembered per project
- Re-offer triggers: 30 days since the answer, or 5 specs/plans written outside managed docs since; `accepted` and `never` are terminal
- Delivered as a tool result the agent relays in conversation — no forced dialog, no dashboard state coupled to a session
- Never blocks the in-flight write, never repeats within one session after an answer, never fires when `decision: disabled` or coverage forbids managed writes

**Detection and relations**

- Backtick extraction → `symbol` / `path` / `anchor_link` classification
- Symbol resolution against the codebase FTS index; path resolution against the filesystem
- Spec↔plan relation from front-matter `spec:` / `supersedes:`, then `**Spec:**` header, then inline link; reverse edge derived by query

**Backend and dashboard**

- `FileSystemWatcher` over the allowed paths, debounced, incremental
- SSE event on completion, same shape the Skills tab consumes
- REST: `GET /api/docs/tree`, `/doc`, `/search`, `/staleness`, `/audit`, `/policy`, `/offer`; `PUT /api/docs/policy`, `/offer`; `POST /api/docs/reindex`
- REST mutations: `POST /api/docs`, `PUT /api/docs/{id}`, `DELETE /api/docs/{id}`, `POST /api/docs/{id}/annotate`, `/promote`, `/relation` — all routed through `DocsService`, external targets rejected with `409 doc_read_only`, every write audit-tagged with the caller's actor
- Docs tab: topic tree with reading order, ownership badge (`Managed` / `External`), origin badge (`Ancestor`), kind chip, plan progress bar, spec approval chip, section TOC, Markdown body, status chips, stale-refs panel linking claimed file/line, audit drawer (managed only), **edit/annotate controls shown only for managed documents**

**Tests**

- Chunking: nested headings, fences, duplicate slugs, preamble
- Front-matter: present, absent, malformed
- Checkbox counting including fenced-code exclusion
- Worktree sharing: all worktrees of one repo share one shard
- Ancestor visibility
- FTS porter and trigram; cascade delete; rebuild-from-empty
- Resolve-positive, resolve-negative (renamed symbol), dead anchor link
- Front-matter error isolation (one broken file does not blank the catalog)
- `decision: disabled` leaves zero repo footprint and routes `doc_save` to the shard
- Default scan indexes only `docs/**` and `.eling/docs/**`; `.superpowers/sdd/` absent
- `exclude` overrides `include`
- Spec↔plan relation resolves both directions
- `external` documents report `ownership = external` and reject `doc_save` / `doc_annotate` in place; `promote: true` flips them to `external`
- Every REST mutation returns `409 doc_read_only` for an `external` target and records a `doc_audit` row for a `managed` one
- A dashboard write and an MCP write of the same change produce identical resulting state and identical audit fields apart from the actor
- `managed` documents under an ancestor scope report `origin = ancestor` and remain writable
- Metadata inference for external documents (kind from folder, topic, status, read_order) with no front-matter written
- Offer policy: fires once from `unasked`; `accepted` and `never` never re-offer; `declined` re-offers after 30 days or 5 specs; stays silent when `decision: disabled`; an unanswered offer does not count as `declined`

### 1.2 Out of scope (this phase)

- **Unification with memory** — docs is never `MemoryKind.Doc`, and memory gains no `kind` field; the two subsystems share helpers, never a table or tool
- **Changing `.gitignore`** — `.eling/docs/` stays ignored, exactly like `.eling/memories/`; the only route to a shared document is `doc_save(promote: true)` into `docs/`
- **Migrating the 153 existing files** — they stay where they are, linked by path; moving them would break historical cross-references and every `docs/...` path in CHANGELOG
- **Semantic (LLM) staleness judging** — resolution is symbol/path lookup only; a judge is a later addition
- **Embeddings or vector search** — retrieval is FTS5, dual-tokenizer, like memory and skills
- **Indexing agent process folders by default** — `.superpowers/sdd/`, `.agents/**`, `.eling/snippets/**` are not indexed until the user adds an include pattern; their existence is not consent
- **Any repo footprint when `decision` is `disabled`** — no folder, no index database, no sidecar, no stray file under `<repo>/.eling/`
- **Rewriting plan checkboxes** — progress is parsed and reported; toggling a step stays an editor/git action
- **Dashboard and desktop mutations are in scope through REST** — writes are no longer MCP-only. The dashboard writes in this phase; the desktop client is expected to consume the same endpoints later. Every HTTP write runs the same ownership, promotion, and audit checks as the MCP path
- **A desktop client itself** — the endpoints are designed for it, but no Avalonia UI for docs is built in this phase
- **Writing to `external` documents** — `docs/` is owned by the repository; Eling reads and indexes it, never edits it. `promote: true` moves a managed document *into* `docs/`, which is a one-way ownership transition, not an in-place edit
- **Persisting annotations or audit for `external` documents** — they would have no home in the file and would vanish on index rebuild, so they are not offered
- **Changing memory, intention, or skill storage, schemas, tools, or behaviour**
- **A wiki rendering engine of our own** — the dashboard reuses the Markdown renderer used by skills
- **Searching across workspaces** — a docs index is per workspace and per tier, unlike the global codebase index
- **Snippets** — a snippet is a single fenced code block with a mandatory `language` and tag-filtered search; a doc is a multi-section prose artefact with a `kind` and section-level retrieval. They sit on different axes, so Snippet Management stays a separate, unimplemented case (see §1.4 and §5). Docs does not gain a `snippet` kind, and no field here is nullable only for one kind.

### 1.3 Target Projects

| Project | What changes |
|---|---|
| `Eling.Core` | New `Docs/` domain: `Doc`, `DocId`, `DocOwnership`, `DocOrigin`, `DocKind`, `DocSection`, `DocRef`, `DocRelation`; optional front-matter parser; external metadata inference; heading chunker + anchor slugger; plan checkbox counter; `DocScopeResolver`; `SqliteDocsIndex`; `DocsService`; reference resolver; scope policy store + pattern matcher. Reuses `ResolveCanonicalProjectRoot` and `ResolveProjectLocalDir` from `Scope/`; no path logic is duplicated. |
| `Eling.Backend` | Eleven MCP tools in `Mcp/Tools/` + `ToolCatalog` registration; `FileSystemWatcher` and SSE publication; REST endpoints under `/api/docs` including mutations; dependency wiring for `ICodebaseIndex` (symbol resolution) and the docs service. |
| `tests/Eling.Core.Tests` | Chunking (nested headings, fences, duplicate slugs, preamble); front-matter present/absent/malformed; plan checkbox counting incl. fenced-code exclusion; worktree shard sharing; ancestor visibility; FTS porter/trigram; cascade delete; rebuild-from-empty; reference resolve-positive / resolve-negative; front-matter error isolation; scope policy pattern matching. |
| `tests/Eling.Backend.Tests` | Tool schema and ownership/origin defaulting for all eleven tools; `promote` path safety; external documents refuse `doc_save` and `doc_annotate`; scope-policy change refused without approval; offer policy states and re-offer triggers; `decision: disabled` leaves zero repo footprint and routes `doc_save` to the shard; default scan contains no `.superpowers/sdd/` entry; watcher incremental reindex and SSE event shape. |
| `src/frontend/Eling.Dashboard` | Docs tab beside Memories, Intentions, and Skills: topic tree with reading order, scope badge, kind chip, plan progress bar, spec approval chip, section TOC, Markdown body, status chips, stale-refs panel, audit drawer. Verified with `pnpm --filter eling-dashboard exec tsc --noEmit`. |

### 1.4 Extends

| Prior work | What is inherited | What stays unchanged |
|---|---|---|
| `docs/nested-memory.md` | Ancestor `.eling` resolution — a sub-folder workspace sees documents registered in an ancestor scope, each marked with the scope identity it came from | Memory scope semantics and its tier order |
| `docs/recall-architecture.md` | The `merged` view concept and how scope-qualified results are presented together | Recall composition, truncation rules, and the `memory_recall` contract |
| `docs/superpowers/specs/2026-10-09-local-only-project-memory-design.md` | `ResolveCanonicalProjectRoot` with git-common worktree sharing; `ResolveProjectLocalDir` and the `DATA/eling/projects/<name>-<hash>/` shard naming; the `ask \| disabled \| clear` machine-local policy shape; the rule that project-bound writes route to the shard rather than failing | Memory's own three tiers and its `memory_project_scope_policy` storage |
| `Eling.Core/Skill/` (implemented, 42 files) | The reference implementation for this subsystem's **shape**: `SkillOrigin.Eling \| External` as a machine-only ownership marker that never round-trips through front-matter; `SkillFileFormat` as the front-matter parser; `SqliteSkillIndex` with dual FTS as the index model; `SkillCatalogScanner` + `ScanReport` as the idempotent, never-abort-on-a-bad-folder scanner; `RebuildIndexAsync` as index-as-cache; `ISkillChangeNotifier` as the live-refresh seam; external rows as "permanently read-only to every mutating path" | Nothing in `Skill/` changes. Docs copies the **pattern**, not the code: separate types, separate index database, separate storage root |
| `docs/superpowers/specs/2026-09-18-snippet-management-design.md` | Markdown files with YAML front-matter as canonical storage; a normalized SQLite index without JSON array columns; dual-scope project/global topology; the shape of a dashboard viewer over Markdown artefacts | Snippets remain unimplemented (`src/backend/Eling.Core/Snippet/` does not exist, and the spec is dated 2026-09-18 with every phase still open). This spec borrows the **storage and indexing pattern only**; it does not implement, absorb, or depend on Snippet Management |

### 1.5 Relationship to the Skill subsystem

Skill is the closest existing subsystem and the deliberate template. Half of what Docs needs
already exists there, so the boundary is stated explicitly rather than left to be rediscovered.

> The full five-way comparison with Memory, Intention, Skill, and Snippet lives in
> [`docs/artifact-subsystems.md`](../../artifact-subsystems.md). This section covers only the
> Skill boundary that directly shapes the implementation.

| Concern | Skill (implemented) | Docs (this spec) | Relationship |
|---|---|---|---|
| Ownership marker | `SkillOrigin.Eling \| External`, index-only | `DocOwnership.Managed \| External`, index-only | Same concept, different vocabulary |
| Canonical storage | `.agents/skills/<name>/SKILL.md` | `.eling/docs/<topic>/<slug>.md` | Same pattern, different root |
| Front-matter parse | `SkillFileFormat` (15 KB) | new `DocFrontMatter` | **Duplicated by intent** — see below |
| Index | `SqliteSkillIndex` (34 KB) | new `SqliteDocsIndex` | **Duplicated by intent** |
| Dual FTS | porter unicode61 + trigram | porter unicode61 + trigram | Same tokenizer pair, same tier-1/tier-2 search |
| Scanner | `SkillCatalogScanner` + `ScanReport` | `DocsService` scan | **Duplicated by intent** |
| Index as cache | `RebuildIndexAsync` | `doc_index` | Same contract |
| Live refresh | `ISkillChangeNotifier` → SSE | watcher → SSE | Same seam |
| Trash / restore | `DeleteAsync` + `RestoreAsync` | not in this phase | Docs has no trash |
| Sibling files | `SkillSiblingFile` | not in this phase | Docs has none |
| Section chunks | absent | `doc_sections` + anchors | **Unique to Docs** |
| Supersession | absent | file status + section annotation | **Unique to Docs** |
| Step progress | absent | plan checkbox counts | **Unique to Docs** |
| Staleness | absent | backtick → codebase index resolution | **Unique to Docs** |

**Why the duplication is intentional.** Extracting a shared `MarkdownCatalog` layer would cut
roughly 60% of the new code, but it means refactoring 42 files of working, tested Skill code for
the benefit of a subsystem that does not exist yet. Docs therefore copies the pattern with its own
types. Consolidation is a deliberate later decision, recorded as technical debt, not an oversight.

**Why `adopt` and `promote` point in opposite directions.** `skill_adopt` takes an
externally-supplied skill and makes Eling own it, because a skill catalog is assembled from
third-party tooling. `doc_save(promote: true)` takes an Eling-owned document and gives it to the
repository, because a document is meant to survive as project documentation. The direction follows
who the artefact belongs to by design, and the asymmetry is correct — not an inconsistency to be
tidied away.



**A snippet is not a doc.** Inheriting the pattern is not the same as sharing the domain, and the
two must not be merged later without a deliberate decision:

| | Snippet | Doc |
|---|---|---|
| Primary content | one fenced code block | a document of many sections |
| Mandatory metadata | `language` | `kind` (spec / plan / doc / scratch) |
| Search axis | full-text plus `language` and tag filters | full-text plus `kind`, `topic`, `status`, `scope` |
| Unit of replacement | the whole snippet | an individual section |
| Relation model | to memories (`implements`, `anti-pattern`, `example`, `test-case`) | to other docs (`implements`, `supersedes`, `references`) |
| Reader | an agent that needs working code to paste | a human reviewing a design, an agent executing a plan |

Folding snippets into docs would mean a `language` column null for every non-snippet kind, a
section chunker that does nothing on a single code block, a `doc_search` parameter that applies to
one kind only, and a dashboard row that needs a code viewer the other kinds never use. That is the
same failure mode this spec rejects for `MemoryKind.Doc`, so the boundary is stated here rather
than rediscovered later.

**What this spec does not disturb:** memory, intention, and skill schemas, storage, tools, and behaviour stay exactly as written. Docs is a fourth independent subsystem that reuses the scope resolver and the dual-FTS scheme rather than replacing either.


## 2. Approaches considered

### 2.1 Separate subsystem vs. extension of memory

1. **Unify — docs become `MemoryKind.Doc`** (rejected): fewest lines, but a plan needs step
   state and a spec needs approval state, neither of which belongs on a small note. The
   unified table would carry columns meaningful for one kind and null for the other, and every
   memory tool would grow doc-shaped parameters.
2. **Docs as a read-only index over `docs/`** (rejected): usable but cannot hold documents the
   agent creates, cannot carry lifecycle metadata, and cannot store resolved references.
3. **Separate subsystem, reusing memory's helpers and patterns** (chosen): distinct domain,
   storage, index, and tools; shared resolver, front-matter parser pattern, dual-FTS scheme,
   and scanner shape. The boundary stays clean and the well-tested path logic is not rewritten.

### 2.2 Where documents live

1. **DB-only** (rejected): docs must stay reviewable in the repo and survive a DB loss, and the
   153 existing files would have to be migrated or duplicated.
2. **Files only, index discarded on every run** (rejected): staleness resolution, section
   hash, plan progress, and audit need somewhere to persist between scans.
3. **Hybrid — files are truth, index DB is a rebuildable cache** (chosen).

### 2.3 Supersession granularity

1. **Per-file only** (rejected): a 34 KB spec typically supersedes *sections* of others.
2. **Per-section only** (rejected): forces front-matter churn across every section.
3. **Per-file status + per-section annotation pointer** (chosen).

### 2.4 Staleness detection

1. **Link and path check only** (rejected as insufficient): misses a renamed class or tool.
2. **LLM judge per section** (rejected for now): costly, non-deterministic, per-section calls.
3. **Backtick symbol and path resolution against the codebase FTS index** (chosen):
   deterministic and free, and precise because Eling documents mark identifiers with backticks.

### 2.5 Discovery

1. **Opt-in via front-matter only** (rejected): requires editing 153 files first.
2. **Every `.md` under the workspace** (rejected): swallows `node_modules` and vendored READMEs.
3. **`docs/` and `.eling/docs/`, front-matter optional** (chosen).

## 3. Architecture

### 3.1 Storage topology

Two independent axes, not one. **Ownership** says who the document belongs to; **origin** says
where it was found. A document in the current repo's `docs/` is `external` — the repository owns
it, not Eling — even though it sits in the workspace the user opened. A document under
`.eling/docs/` is `managed`, and stays managed whether it is found in the current repo or in an
ancestor.

| Path | `ownership` | `origin` | Eling may write |
|---|---|---|---|
| `<repo>/docs/**/*.md` | `external` | `current` | no |
| `<repo>/.eling/docs/<topic>/<slug>.md` | `managed` | `current` | yes |
| `<ancestor>/.eling/docs/**/*.md` | `managed` | `ancestor` | yes (through the ancestor scope) |
| `<ancestor>/docs/**/*.md` | `external` | `ancestor` | no |
| `DATA/eling/projects/<name>-<hash>/docs/` | `managed` | `shard` | yes |
| `CONFIG/eling/docs/` | `managed` | `global` | yes |

The `merged` view orders `managed` before `external` at equal relevance, and `current` before
`ancestor`, matching memory's scope precedence.

`ancestor` resolution reuses the existing ancestor `.eling` lookup from memory: a sub-folder
workspace sees managed documents registered in an ancestor scope, marked with the origin that
identifies where they came from.

**What ownership gates:**

- `doc_save` and `doc_annotate` write only `managed` documents. An `external` document is opened
  for reading, never for writing.
- `doc_audit` records `managed` writes only. There is nothing to audit in a file Eling never
  touched.
- `promote: true` is the deliberate transition: it writes the document into `docs/`, which makes
  it `external` from that point on. Promotion is one-way by design — becoming external means the
  repository owns it and Eling stops writing.
- `external` metadata (`kind`, `topic`, `status`, `read_order`) is **inferred** from location,
  heading structure, and filename. Eling never writes front-matter into an `external` file, and
  never persists an annotation that has no home in the file — an annotation on an external
  document would vanish on index rebuild, so it is not offered.
- `decision: disabled` removes Eling's managed footprint; it does not change ownership of
  `docs/`, which stays `external` and stays indexed per coverage patterns.

### 3.2 Index placement

Follows the existing per-tier pattern:

- managed, `origin = shard` → `DATA/eling/projects/<name>-<hash>/docs.db` (beside `memory.db`)
- managed, `origin = current` → `<repo>/.eling/docs-index.db` (ignored, rebuildable) — **only when
  `decision` is not `disabled`; a disabled scope creates neither the folder nor this database**
- managed, `origin = global` → `DATA/eling/docs.db`
- `external` documents are indexed into whichever database covers the workspace that reads them;
  they have no store of their own, because Eling does not own them

Every index is a **cache**. Deleting one loses nothing for `managed` documents (they rebuild from
their Markdown) and loses nothing at all for `external` ones, whose metadata was inferred rather
than stored.

### 3.3 Schema

```sql
CREATE TABLE docs (
  id            TEXT PRIMARY KEY,       -- "<ownership>:<origin>:<sha1(rel_path)>"
  ownership     TEXT NOT NULL,          -- managed | external
  origin        TEXT NOT NULL,          -- current | ancestor | shard | global
  rel_path      TEXT NOT NULL,
  kind          TEXT NOT NULL,          -- spec|plan|doc|scratch
  title         TEXT,
  status        TEXT NOT NULL DEFAULT 'active',
                -- spec:    draft|review|approved|implemented|superseded|archived
                -- plan:    draft|active|done|superseded|archived
                -- doc:     draft|active|superseded|archived
                -- scratch: archived (default) | active | superseded
  topic         TEXT,
  read_order    INTEGER,
  superseded_by TEXT,                   -- rel_path of replacement
  steps_total   INTEGER,                -- plan only, from parsed checkboxes
  steps_done    INTEGER,                -- plan only
  file_hash     TEXT NOT NULL,
  mtime         TEXT NOT NULL,
  updated_at    TEXT NOT NULL
);

CREATE TABLE doc_sections (
  doc_id       TEXT NOT NULL REFERENCES docs(id) ON DELETE CASCADE,
  anchor       TEXT NOT NULL,           -- slug of heading, unique within doc
  heading      TEXT NOT NULL,
  level        INTEGER NOT NULL,
  line_start   INTEGER NOT NULL,
  line_end     INTEGER NOT NULL,
  content_hash TEXT NOT NULL,
  status       TEXT NOT NULL DEFAULT 'active',
  annotation   TEXT,                    -- e.g. 'superseded by x.md#y'
  steps_total  INTEGER,                 -- plan sections only
  steps_done   INTEGER,
  PRIMARY KEY (doc_id, anchor)
);

CREATE TABLE doc_refs (
  doc_id     TEXT NOT NULL,
  anchor     TEXT NOT NULL,
  ref_kind   TEXT NOT NULL,             -- symbol|path|anchor_link
  ref_value  TEXT NOT NULL,
  resolved   INTEGER NOT NULL DEFAULT 0,
  resolved_at TEXT,
  PRIMARY KEY (doc_id, anchor, ref_kind, ref_value)
);

CREATE TABLE doc_audit (
  id          TEXT PRIMARY KEY,
  doc_id      TEXT NOT NULL,
  action      TEXT NOT NULL,            -- create|update|edit|supersede|annotate|promote
  actor       TEXT NOT NULL,            -- 'user' | 'mcp:<tool>' | agent session id
  before_hash TEXT,
  after_hash  TEXT,
  at          TEXT NOT NULL
);

CREATE VIRTUAL TABLE sections_fts USING fts5(
  doc_id UNINDEXED, anchor UNINDEXED, heading, body,
  tokenize='porter unicode61 remove_diacritics 1');

CREATE VIRTUAL TABLE sections_fts_trigram USING fts5(
  doc_id UNINDEXED, anchor UNINDEXED, heading, body, tokenize='trigram');
```

Two FTS layers are mandatory: they are the tokenizer pair used by memory and skills, and
search behaviour must be consistent across subsystems. Query tier 1 with AND; fall back to OR
when fewer than three hits come back.

### 3.4 Front-matter contract

Front-matter belongs to `managed` documents only. An `external` document's `kind`, `topic`,
`status`, and `read_order` are **inferred** — from its folder, its first heading, its filename,
and its position — because Eling never writes into a file it does not own.

For a managed document, front-matter is still optional: without it, `kind` is inferred from
location (`.eling/docs/specs/` → `spec`, `.eling/docs/plans/` → `plan`, any non-default policy
pattern → `scratch`, otherwise `doc`), `status` defaults per kind, and `topic` comes from the
topic folder.

```yaml
---
kind: spec
topic: memory-recall
order: 3
status: approved
supersededBy: docs/superpowers/specs/2026-11-04-memory-recall.md
sections:
  "old-contract":
    annotation: "superseded by memory-recall.md#contract"
---
```

A malformed front-matter block is **not** fatal (unlike memory): the document is indexed with a
`frontmatter_error` finding so one broken file cannot blank the catalog.

### 3.5 Section chunking, anchors, and plan progress

- Split on ATX headings (`#` … `######`). Content before the first heading becomes `_preamble`
  when non-empty.
- Anchor = GitHub-flavoured slug: lowercase, spaces to `-`, strip punctuation, dedupe with
  `-1`, `-2` — matching what existing links in the corpus already assume.
- A section owns lines from its heading to the line before the next heading of equal or lower
  level. Sections are flat and addressable; nested headings do not re-parent content.
- Fenced code blocks are scanned for heading-like lines and never split on them.
- **Plan progress:** within a `plan`, top-level `- [ ]` / `- [x]` items are counted per section
  and aggregated to the document. Items inside fenced code are ignored. A plan's derived
  `status` becomes `done` when `steps_done == steps_total > 0`, and `active` otherwise — unless
  front-matter declares `status` explicitly, which always wins.

### 3.6 Reference resolution

| Kind | Source | Resolution |
|---|---|---|
| `symbol` | backtick spans matching an identifier shape | codebase FTS index, exact symbol tier |
| `path` | backtick spans containing `/` or ending `.md`/`.cs`/`.ts`/`.json` | filesystem existence, repo-relative |
| `anchor_link` | Markdown links `[text](path#anchor)` | target doc indexed **and** anchor present |

Unresolvable references are recorded with `resolved = 0` and surfaced by `doc_staleness`. A
later scan flips them to `1` when the symbol reappears — findings are recomputed, never
accumulated. Resolution is conservative: plain prose in backticks (`` `true` ``, `` `==` ``,
CLI flags) is filtered before resolution to keep the report readable.

### 3.7 MCP tools

| Tool | Default view | Contract |
|---|---|---|
| `doc_search` | merged | Section-level FTS; filters `ownership`, `origin`, `kind`, `topic`, `status`, `pathPrefix`; returns `rel_path`, `anchor`, `heading`, snippet, `status`, `ownership`, `origin`. Never returns whole documents. |
| `doc_read` | merged | One section by `rel_path` + `anchor`; without `anchor`, the whole document. `mode: 'map'` returns the heading tree with line ranges and per-section plan progress. |
| `doc_list` | merged | Documents grouped by `topic`, ordered by `read_order` then path; `ownership`, `origin`, `kind`, and plan progress included. |
| `doc_save` | managed | Create or update a **managed** document of a given `kind`. Writes front-matter automatically. `promote: true` writes into `docs/` instead, making the document `external` from then on. Refuses to edit an `external` document in place. Records audit. |
| `doc_annotate` | — | Set per-section `status` and `annotation` on a **managed** document without rewriting the body. Refused for `external`. |
| `doc_staleness` | merged | Findings: unresolved refs, superseded-without-replacement, `draft` older than 30 days, documents untouched for 180 days. Covers `external` documents too, since a stale reference in a repo spec is worth knowing. |
| `doc_audit` | — | Recent audit entries for a managed document or the whole scope. Always empty for `external`. |
| `doc_index` | — | Incremental rescan; skips files whose `file_hash` is unchanged. Scans only the paths the scope policy allows. |
| `doc_relation` | — | Read and write spec↔plan edges (`implements`, `supersedes`, `references`); returns both directions. Edges may be read between any documents, but are only written between managed ones. |
| `doc_scope_policy` | — | Inspect or change the coverage policy (§3.11). Adding an include pattern requires the user's approval, exactly like `memory_project_scope_policy`. |
| `doc_offer_managed` | project | Read, raise, or answer the managed-docs recommendation (§3.14). Returns the current `offer` state and, when a trigger fires, an offer object for the agent to relay in conversation. Answering records `accepted` / `declined` / `never` with a timestamp and resets `specsSinceAsk`. |

### 3.8 Watcher and live refresh

A debounced `FileSystemWatcher` over `docs/` and `.eling/docs/`, re-indexing only changed files
and publishing the same SSE event shape the Skills tab already consumes.

### 3.9 REST surface

Reads and writes are split by ownership, not by transport. The dashboard writes today; the desktop
client is expected to write later, so the endpoint surface is designed as the future mutation path
rather than as a reader-only view. A write over HTTP runs through the **same** `DocsService`
checks an MCP call does — ownership, promotion, audit — so the transport never becomes a way
around the rules.

```
GET  /api/docs/tree?ownership=&origin=&kind=&topic=
GET  /api/docs/doc?path=...&anchor=...
GET  /api/docs/search?q=...&kind=...&ownership=...
GET  /api/docs/staleness
GET  /api/docs/audit?path=...
GET  /api/docs/policy
PUT  /api/docs/policy
GET  /api/docs/offer
PUT  /api/docs/offer
POST /api/docs/reindex

POST   /api/docs                         create a managed document
PUT    /api/docs/{id}                    update a managed document
DELETE /api/docs/{id}                    archive a managed document
POST   /api/docs/{id}/annotate           set per-section status and annotation (managed only)
POST   /api/docs/{id}/promote            transition managed → external into docs/
POST   /api/docs/{id}/relation           add a spec↔plan edge between managed documents
```

Rules carried from the service layer, identical to the MCP path:

- `PUT`, `DELETE`, `annotate`, `promote`, and `relation` return **409 Conflict** with code
  `doc_read_only` when the target is `external` — the same shape `SkillEndpoints` already uses for
  `skill_read_only`, so one dashboard client can handle both.
- Every accepted write records a `doc_audit` row with `actor = "dashboard"` (or the desktop client's
  identity), so a change made through the UI is as attributable as one made through MCP.
- No endpoint writes front-matter into an `external` document. `promote` is the only path that
  produces a file under `docs/`, and it is a one-way ownership transition.
- The policy `PUT` is a full replacement, matching the `PUT /api/shell/config` convention already
  in the backend; adding an include pattern requires the same approval the MCP tool demands.

### 3.10 Dashboard

A **Docs** tab beside Memories, Intentions, and Skills:

- Topic tree with reading order; **ownership badge** (`Managed | External`) and **origin badge**
  (`Current | Ancestor | Shard | Global`) so a reader always knows who owns the document and where
  it came from; `kind` chip (`spec` / `plan` / `doc` / `scratch`).
- **Plan rows show a progress bar** from `steps_done / steps_total`.
- **Spec rows show the approval chip** (`draft` / `review` / `approved` / `implemented`).
- Section TOC sidebar, Markdown body rendered by the existing renderer.
- Status chips: `active`, `superseded` (muted, with replacement link), `stale: N` (error tone).
- `external` documents render **read-only**: no edit control, no annotate control, no audit drawer,
  and inferred metadata is labelled as inferred so the reader does not mistake it for declared
  front-matter. `managed` documents expose an editor and annotate controls that call the REST
  mutations in §3.9.
- A rejected write surfaces as a clear read-only message rather than a generic error, matching how
  the Skills page already reports `skill_read_only`.
- Stale-refs panel listing unresolved symbols and paths, each linking to the claimed file/line.
- Audit drawer per document.

### 3.11 Scope policy (user-held)

Two independent decisions. Coverage (`include`/`exclude`) answers *which files are read*; the
project-scope decision answers *whether Eling may write into this repo at all*. Conflating them
is what makes a disabled scope leak a folder anyway.

```json
{
  "decision": "ask",              // ask | disabled | clear
  "default": ["docs/**", ".eling/docs/**"],
  "include": [],
  "exclude": []
}
```

**The `decision` field governs Eling's managed footprint — `<repo>/.eling/docs/` and nothing
else.** It is the same machine-local decision shape as `memory_project_scope_policy`, with the
same three values.

| `decision` | `<repo>/.eling/docs/` | Managed writes land in | `external` `docs/**` |
|---|---|---|---|
| `ask` (default) | created on first need, after consent | `.eling/docs/<topic>/` | indexed as `external` |
| `disabled` | **never created — not even opened** | `DATA/eling/projects/<name>-<hash>/docs/` (shard) | **indexed as `external`, unaffected** |
| `clear` | override removed, back to `ask` | — | — |

The crucial line is the last one: **`decision` does not govern `docs/`.** `docs/` is an
`external` artefact the repository owns; whether it is indexed is decided by the coverage patterns
alone. A repo can therefore run `decision: disabled` and still have all 153 documents searchable —
Eling simply never writes there.

Rules:

- **Default coverage is exactly `docs/**` and `.eling/docs/**`.** Nothing else is scanned.
- **`include` is empty by default.** `.superpowers/sdd/**`, `.agents/**`, `.eling/snippets/**`
  and comparable folders are **not** indexed until the user adds a pattern. Their existence is
  not consent.
- **`exclude` always wins** over `include` and over the defaults, so a user can carve a folder
  back out without revoking the rest.
- Patterns are repo-relative, `**`-capable, and evaluated with the same matcher the codebase
  indexer uses.
- When `decision` is `disabled`, no code path may create `<repo>/.eling/docs/`, create
  `<repo>/.eling/docs-index.db`, or touch any file under either. The write path routes to the
  local shard instead, exactly as project-bound memory writes route to
  `DATA/eling/projects/<name>-<hash>/`.
- Opting in is recorded as a user decision, and `doc_list` reports which patterns and which
  decision are active, so coverage is never a mystery.
- Any document discovered from a non-default pattern gets `kind = scratch` unless front-matter
  declares otherwise, and `status = archived` by default — searchable on demand via
  `doc_search(kind: 'scratch')`, but absent from the default `doc_list` view.

Rationale: agent process trees accumulate hundreds of short-lived files (`progress.md`,
`task-N-brief.md`, `task-N-report.md`, `review-N-package.md`). Indexing them alongside specs and
plans would drown the catalogue in review chatter. They are valuable when explicitly asked for,
and noise when not.

### 3.12 Spec ↔ plan relation

A plan declares the spec it implements; the relation is stored both ways.

```yaml
# on a plan
kind: plan
spec: docs/superpowers/specs/2026-11-20-docs-management-design.md
```

```sql
CREATE TABLE doc_relations (
  from_doc   TEXT NOT NULL,
  to_doc     TEXT NOT NULL,
  kind       TEXT NOT NULL,          -- implements|supersedes|references
  PRIMARY KEY (from_doc, to_doc, kind)
);
```

Sources, in order of precedence: front-matter `spec:` / `supersedes:`, then a `**Spec:**`
header line as already used in the corpus, then an inline link to a `kind: spec` document.

The reverse edge is derived, not stored twice: a spec's implementing plans are a query over
`doc_relations` where `to_doc` is the spec. The dashboard shows both directions — a spec lists
its plans with their progress, a plan shows the spec with its approval status.

### 3.13 Planner guidance (opt-in, not enforced)

Where Superpowers drives the work, `.superpowers/sdd/<feature>/progress.md` and the per-task
brief/report/review files remain the tool's own artefact. If the user opts that tree in via
§3.11, those documents arrive as `kind: scratch` and the spec↔plan relation links them to the
plan they serve. Without opt-in, they are simply not Eling's concern.

### 3.14 Offer policy — recommending managed docs for specs and plans

Once Docs exists, Eling is in a position to *recommend* that a spec or a plan be written as a
managed document rather than into `docs/` or `.superpowers/`. The recommendation is an offer, never
an action: Eling proposes, the user decides, and the decision is remembered.

This is a **third, separate control**, not a variant of §3.11:

| Control | Question it answers | Where it lives |
|---|---|---|
| Coverage (`include` / `exclude`) | Which files are **read** | §3.11 |
| `decision` (`ask` / `disabled` / `clear`) | Whether Eling may **write** into this repo | §3.11 |
| `offer` (this section) | Whether Eling **recommends** managed docs when a spec or plan is about to be written | offer policy, per project |

```json
{
  "offerManagedDocs": "unasked",   // unasked | accepted | declined | never
  "askedAt": null,
  "answeredAt": null,
  "specsSinceAsk": 0
}
```

**States**

| `offerManagedDocs` | Eling behaviour |
|---|---|
| `unasked` | Offer once, on the first spec-or-plan write Eling observes in this project |
| `accepted` | Stop offering; write managed documents as the default for specs and plans |
| `declined` | Stop offering; do not raise it again until a re-offer trigger fires |
| `never` | Never offer again in this project, under any trigger. The user asked to be left alone |

**Re-offer triggers** — a `declined` decision is remembered but not frozen. Eling raises the offer
again only when one of these is true, and only once per trigger:

- **Time:** `answeredAt` is older than **30 days**.
- **Volume:** `specsSinceAsk` reaches 5 — five specs or plans have been written outside managed
  docs since the decision was given.

The 30-day threshold is fixed, not a placeholder. A month of unmanaged specs and plans is enough
for a `docs/` tree to become unmanageable — the corpus this spec was written against reached 153
files in roughly three months — so the first interval should be short enough to catch that drift
while it is still cheap to correct. The 5-document count is the companion guard for a repository
that writes far more than one spec a month.

`accepted` and `never` ignore both triggers. They are terminal until the user changes them.

**How the offer is delivered.** Through a tool result, not a UI interruption. When an agent is
about to write a spec or plan, `doc_offer_managed` returns:

```json
{
  "offer": "managed-docs",
  "reason": "This is the 5th spec written outside managed docs since you declined on 2026-10-21.",
  "options": ["accept", "decline", "never"],
  "scope": "project"
}
```

The **agent** relays this in conversation and records the answer with the same tool. There is no
forced dialog and no dashboard state synchronised with the session — the agent already owns the
conversation, so it carries the question.

**What the offer may never do:**

- It never blocks the write the agent was doing. The user's answer changes *where the next document
  goes*, not whether the current one happens.
- It never repeats within one session after being answered. One ask, one answer.
- It never overrides `decision: disabled` or §3.11 coverage. If Eling may not write managed docs at
  all, there is nothing to offer and the tool says so.
- It is never inferred from silence. "No answer" is `unasked`, and an unanswered offer is re-raised
  only by the triggers above.

**Why this is an offer and not a default.** Managed docs are the better home for a spec or plan —
they carry approval state, step progress, supersession, and staleness findings. But the user may
have reasons Eling cannot see: a team that reviews specs in `docs/` through pull requests, a
pipeline that reads `.superpowers/sdd/`, or simply a preference for keeping Eling's footprint out of
the repository. The recommendation exists because the trade-off is real; the consent gate exists
because the user owns it.

## 4. Implementation phases

| # | Phase | Verification |
|---|---|---|
| 1 | `Eling.Core/Docs` domain: `Doc`, `DocId`, `DocOwnership`, `DocOrigin`, `DocKind`, `DocSection`, `DocRef`, optional front-matter parser, external metadata inference, heading chunker, anchor slugger, checkbox counter | unit tests: nested headings, fences, duplicate slugs, preamble; front-matter present/absent/malformed; checkbox counting incl. fenced-code exclusion; external inference produces kind/topic/status without writing front-matter |
| 2 | `DocScopeResolver` — resolves `ownership` and `origin`; **reuse** `ResolveCanonicalProjectRoot` and the project-local shard helpers; no duplicated path logic | `docs/` resolves `external` + `current`; `.eling/docs/` resolves `managed` + `current`; ancestor `.eling/docs/` resolves `managed` + `ancestor`; all worktrees of one repo share one shard |
| 3 | `SqliteDocsIndex` — schema, dual FTS, tier query, migration handling, modelled on `SqliteSkillIndex` | FTS porter/trigram, cascade delete, rebuild-from-empty |
| 4 | `DocsService` — scan, incremental by hash, section extraction, kind inference, plan progress, reference resolver against `ICodebaseIndex`, staleness report, audit write | resolve-positive, resolve-negative (renamed symbol), dead anchor link, front-matter error isolation |
| 5 | Eleven MCP tools + `ToolCatalog` registration, including `doc_relation`, `doc_scope_policy`, and `doc_offer_managed` | tool schema tests, scope defaulting, `promote` path safety, policy change refused without approval |
| 6 | Scope policy store + pattern matcher + offer policy store; `.superpowers/sdd/` verified **absent** from a default scan; `decision: disabled` verified to leave **zero repo footprint** | default scan indexes only `docs/**` and `.eling/docs/**`; opt-in adds scratch as `archived`; `exclude` overrides `include`; with `disabled`, no `.eling/docs/` and no `.eling/docs-index.db` are ever created, `doc_save` lands in the shard, and `docs/**` remains indexed; the offer fires once when `unasked`, treats `accepted`/`never` as terminal, re-raises on the 30-day or 5-spec trigger, and stays silent under `decision: disabled` |
| 6 | Scope policy store + pattern matcher; `.superpowers/sdd/` verified **absent** from a default scan; `decision: disabled` verified to leave **zero repo footprint** | default scan indexes only `docs/**` and `.eling/docs/**`; opt-in adds scratch as `archived`; `exclude` overrides `include`; with `disabled`, no `.eling/docs/` and no `.eling/docs-index.db` are ever created, `doc_save` lands in the local shard, and `docs/**` remains indexed |
| 7 | Spec↔plan relation: front-matter `spec:`, `**Spec:**` header, inline link; reverse edges | plan→spec and spec→plans both resolve; implements/supersedes distinguished |
| 8 | Watcher + SSE incremental refresh | changed-file-only reindex, debounce, event shape parity with Skills |
| 9 | REST endpoints (reads + mutations) + dashboard Docs tab with editor | `pnpm --filter eling-dashboard exec tsc --noEmit`; no `pnpm build` while the dev server runs on 4427; external-target writes answer 409 `doc_read_only`; every accepted write leaves an audit row |

## 5. Risks and open questions

- **`.gitignore` unchanged means `.eling/docs/` is machine-only.** Accepted. `doc_save(promote: true)`
  into `docs/` is the only route to a shared document, and it always produces a reviewable diff.
  The one-line `!.eling/docs/` negation remains the escape hatch — deliberately not taken now.
- **Default coverage is deliberately narrow.** `docs/**` and `.eling/docs/**` only. Everything
  else requires the user to opt in through the scope policy, so a tool's process folder is never
  mistaken for documentation. The cost is that a user who wants `.superpowers/sdd/` searchable
  must say so once.
- **Kind inference is positional.** Files outside the conventional folders default to `doc`
  unless a non-default policy pattern made them `scratch`; this is a safe default, not a guess.
- **Backtick extraction precision.** Eling's corpus marks identifiers with backticks by
  convention, which makes the heuristic work here; imported third-party Markdown may produce
  more false positives. Mitigated by the identifier-shape filter and by reporting only definite
  absences.
- **153 files with no front-matter** initially get folder-derived topics; curated topics arrive
  incrementally, file by file, without a migration.
- **Section anchors drift** when headings are renamed; stored `anchor_link` references then
  resolve to `0` and surface as findings rather than failing silently.
- **The offer can become noise if the triggers are wrong.** The 30-day interval is a fixed decision,
  not a tuning knob to be second-guessed at implementation time; the 5-document count is its
  companion for repositories that write faster. The guard rails are that `accepted` and `never` are
  terminal, an unanswered offer is never treated as a refusal, and the offer never blocks the
  in-flight write. If it still proves irritating, widen the thresholds — do not remove the user's
  ability to choose.
- **Two write paths must not diverge.** MCP tools and REST mutations both call `DocsService`, and
  the ownership, promotion, and audit checks live in that service rather than in either caller. A
  future endpoint that bypasses it would quietly let `external` documents be edited; the test in
  §1.1 that compares a dashboard write against an MCP write of the same change exists to catch that.
- **Deliberate duplication against the Skill subsystem.** Ownership marking, front-matter
  parsing, the dual-FTS index, and the catalog scanner all exist in `Eling.Core/Skill/` and are
  re-implemented here rather than extracted into a shared `MarkdownCatalog` layer. The extraction
  would cut roughly 60% of the new code but requires refactoring 42 files of working, tested Skill
  code for a subsystem that does not exist yet. Recorded as technical debt with a named future
  decision, not as an oversight (§1.5).
- **Snippet Management is a separate, still-open case.** It is borrowed for its storage pattern
  only (front-matter files, normalized SQLite index, dual-scope topology) and is not implemented
  here. A future decision could merge it, but that requires a deliberate change — the `language`
  axis and single-code-block shape are real differences, not cosmetic ones (§1.4).
- **Open:** should `doc_staleness` be computed during `doc_index` or on demand? Proposed: refs
  already extracted resolve during indexing; the full report is computed on demand.
