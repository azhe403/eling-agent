# Docs Management Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **Source spec:** [docs/superpowers/specs/2026-11-20-docs-management-design.md](../specs/2026-11-20-docs-management-design.md)
> **Status:** `Proposed` → will become `In Progress` on first commit

---

## Phase 0 — Baseline & Guardrails

- [ ] Run baseline build: `dotnet build --tl` (capture duration)
- [ ] Verify no pre-existing `Eling.Core/Docs/` or `Mcp/Tools/Docs*.cs` or `Endpoints/DocsEndpoints.cs`
- [ ] Record commit baseline: `git log -1 --oneline`

---

## Phase 1 — Eling.Core/Docs Domain ✅ **COMPLETED**

**Target:** `src/backend/Eling.Core/Docs/` — pure domain, no infrastructure beyond `Ulid`, `YamlDotNet`, `Microsoft.Data.Sqlite`

- [x] 1.1 `DocId` (ULID + ownership/origin encoding) — `struct`, `ToString()`, `Parse()`
- [x] 1.2 `DocOwnership` enum (`Managed`, `External`)
- [x] 1.3 `DocOrigin` enum (`Current`, `Ancestor`, `Shard`, `Global`)
- [x] 1.4 `DocKind` enum (`Spec`, `Plan`, `Doc`, `Scratch`)
- [x] 1.5 `DocStatus` per kind — `SpecStatus`, `PlanStatus`, `DocStatus`, `ScratchStatus`
- [x] 1.6 `Doc` entity — id, ownership, origin, rel_path, kind, title, status, topic, order, superseded_by, steps_total, steps_done, file_hash, mtime
- [x] 1.7 `DocSection` entity — doc_id, anchor, heading, level, line_start, line_end, status, annotation
- [x] 1.8 `DocRef` entity — doc_id, anchor, raw_text, resolved_path, resolved_line, resolved
- [x] 1.9 `DocRelation` entity — source_id, target_id, kind (`Implements`, `Supersedes`, `References`)
- [x] 1.10 Front-matter parser (`DocFrontMatterParser`) — `kind`, `topic`, `order`, `status`, `supersededBy`, `spec`, `sections{}`, **malformed = non-fatal finding**
- [x] 1.11 External metadata inference (`ExternalDocInference`) — `kind` from folder, `topic` from parent folder, `status` default per kind, `read_order` from filename/heading — **never writes front-matter**
- [x] 1.12 Heading chunker — ATX headings, `_preamble` section, fenced code never splits, GitHub-flavoured anchors with `-1`/`-2` dedupe
- [x] 1.13 Plan checkbox counter — `- [ ]` / `- [x]` per section and per document, **fenced code excluded**
- [x] 1.14 Unit tests:
    - [x] Nested headings + fences + duplicate slugs + preamble
    - [x] Front-matter: present / absent / malformed / empty
    - [x] Checkbox counting with fenced-code exclusion
    - [x] External inference produces kind/topic/status without writing front-matter

**Verification:** All 1067 tests pass, build succeeds, no new warnings in Docs domain.

---

## Phase 2 — DocScopeResolver ✅ **COMPLETED**

**Target:** `src/backend/Eling.Core/Docs/DocScopeResolver.cs` — **reuse** `ResolveCanonicalProjectRoot` and project-local shard helpers; **no duplicated path logic**

- [x] 2.1 `DocScopeResolver` class with `Resolve(path)` → `DocScopeResolution(Ownership, Origin, RelativePath, RootPath)`
- [x] 2.2 Reuse `Eling.Core.Scope.CanonicalProjectRoot.Resolve()`
- [x] 2.3 Reuse project-local shard naming via `ElingPaths.ResolveProjectLocalDir()`
- [x] 2.4 Ancestor `.eling/docs` resolution — walks up from workspace
- [x] 2.5 `GetIndexDbPath(DocOrigin, DocScopeDecision)` for index placement
- [x] 2.6 Verification:
    - [x] `docs/` → `External` + `Current`
    - [x] `.eling/docs/` → `Managed` + `Current`
    - [x] Ancestor `.eling/docs/` → `Managed` + `Ancestor`
    - [x] All worktrees of one repo share one shard

**Verification:** All 1076 tests pass, build succeeds.

---

## Phase 3 — SqliteDocsIndex ✅ **COMPLETED**

**Target:** `src/backend/Eling.Core/Docs/Storage/SqliteDocsIndex.cs` — modelled on `SqliteSkillIndex`

- [x] 3.1 Schema (`docs`, `doc_sections`, `doc_refs`, `doc_relations`, `doc_audit`, `doc_policy`, `doc_offer`)
- [x] 3.2 Dual FTS5: porter `unicode61` (tier-1) + trigram (tier-2) — contentless FTS5
- [x] 3.3 Cascade delete on doc removal
- [x] 3.4 `RebuildAsync()` from empty
- [x] 3.5 Migration handling (version table - implicit via schema creation)
- [x] 3.6 Index placement per origin:
    - [x] `Shard` → `DATA/eling/projects/<name>-<hash>/docs.db`
    - [x] `Current` → `<repo>/.eling/docs-index.db` (**only when decision ≠ disabled**)
    - [x] `Global` → `DATA/eling/docs.db`
    - [x] `External` → indexed into workspace's covering DB; no own store
- [x] 3.7 Verification:
    - [x] FTS porter + trigram queries return correct tier ordering
    - [x] Cascade delete removes sections, refs, relations, audit
    - [x] Rebuild from empty produces same row counts
    - [x] 35 comprehensive unit tests covering all CRUD, search, relations, audit, policy, offer state

---

## Phase 4 — DocsService ✅ **COMPLETED**

**Target:** `src/backend/Eling.Core/Docs/DocsService.cs`

- [x] 4.1 Scan + incremental by `file_hash` (skip unchanged)
- [x] 4.2 Section extraction + kind inference (managed: front-matter; external: positional)
- [x] 4.4 Plan progress (`steps_done` / `steps_total`)
- [x] 4.5 Reference resolver against `ICodebaseIndex` — conservative, identifier-shape filter
- [x] 4.6 Staleness report: unresolved refs, superseded-without-replacement, `draft` > 30 days, untouched > 180 days
- [x] 4.7 Audit write (every mutation: `doc_save`, `doc_annotate`, `promote`, `relation`)
- [x] 4.8 External documents: refuse `save`, `annotate` in place; `promote` flips to `External` into `docs/`
- [x] 4.9 Offer policy: `unasked` → offer once; `accepted`/`never` terminal; `declined` re-offer on 30-day or 5-spec trigger
- [x] 4.10 Verification:
    - [x] Resolve positive (existing symbol)
    - [x] Resolve negative (renamed symbol)
    - [x] Dead anchor link
    - [x] Front-matter error isolation (one bad file ≠ blank catalog)

**Verification:** All 1111 tests pass, build succeeds.

---

## Phase 5 — Eleven MCP Tools

**Target:** `src/backend/Eling.Backend/Mcp/Tools/Docs*.cs` + `ToolCatalog` registration

- [ ] 5.1 `doc_search` — section-level FTS, filters `ownership`, `origin`, `kind`, `topic`, `status`, `pathPrefix`
- [ ] 5.2 `doc_read` — section / whole doc / `mode: map` heading tree
- [ ] 5.3 `doc_list` — grouped by topic, ordered by `read_order`, with ownership, origin, kind, plan progress
- [ ] 5.4 `doc_save` — create/update managed doc, auto front-matter; `promote: true` writes to `docs/`
- [ ] 5.5 `doc_annotate` — per-section status + annotation on managed; refused for external
- [ ] 5.6 `doc_staleness` — findings; covers external too
- [ ] 5.7 `doc_audit` — entries per managed doc or scope; empty for external
- [ ] 5.8 `doc_index` — incremental rescan within allowed scope
- [ ] 5.9 `doc_relation` — read/write spec↔plan edges both directions; write only between managed
- [ ] 5.10 `doc_scope_policy` — inspect/change coverage + decision; include requires approval
- [ ] 5.11 `doc_offer_managed` — read/raise/answer offer policy; project-scoped state
- [ ] 5.12 Tool schema tests, scope defaulting, `promote` path safety, policy change refused without approval

---

## Phase 6 — Scope Policy + Offer Policy Stores

**Target:** `src/backend/Eling.Core/Docs/Policy/` + `SqliteDocsIndex` policy tables

- [ ] 6.1 `DocScopePolicyStore` — include/exclude patterns, `decision` (`ask`/`disabled`/`clear`), machine-local JSON
- [ ] 6.2 Pattern matcher — default scans only `docs/**` and `.eling/docs/**`; `exclude` overrides `include`
- [ ] 6.3 `DocOfferPolicyStore` — `unasked`/`accepted`/`declined`/`never`, `askedAt`, `answeredAt`, `specsSinceAsk`
- [ ] 6.4 `.superpowers/sdd/` verified **absent** from default scan
- [ ] 6.5 `decision: disabled` verified: **zero repo footprint** — no `.eling/docs/`, no `.eling/docs-index.db`, `doc_save` lands in shard, `docs/**` remains indexed
- [ ] 6.6 Offer fires once when `unasked`; `accepted`/`never` terminal; re-raises on 30-day or 5-spec trigger; silent under `decision: disabled`
- [ ] 6.7 Opt-in adds scratch as `archived` by default

---

## Phase 7 — Spec ↔ Plan Relation

**Target:** `DocsService` relation methods + `DocRelation` entity

- [ ] 7.1 Front-matter `spec:` field → forward edge `Implements`
- [ ] 7.2 `**Spec:**` header in plan → reverse edge
- [ ] 7.3 Inline link `[Spec](...)` → edge `References`
- [ ] 7.4 `doc_relation` tool reads both directions
- [ ] 7.5 Verification: plan→spec and spec→plans both resolve; `Implements` vs `Supersedes` distinguished

---

## Phase 8 — Watcher + SSE

**Target:** `src/backend/Eling.Backend/Mcp/Tools/DocsWatcher.cs` + SSE publication

- [ ] 8.1 `FileSystemWatcher` on indexed paths
- [ ] 8.2 Changed-file-only reindex + debounce (500ms)
- [ ] 8.3 SSE event shape parity with Skills (`docs.changed`, `docs.indexed`)
- [ ] 8.4 Verification: edit a managed doc → SSE fires → `doc_search` reflects change

---

## Phase 9 — REST Endpoints + Dashboard Docs Tab

**Target:** `src/backend/Eling.Backend/Endpoints/DocsEndpoints.cs` + `eling-dashboard/src/app/docs/`

- [ ] 9.1 Reads: `GET /api/docs/tree`, `/doc`, `/search`, `/staleness`, `/audit`, `/policy`, `/offer`
- [ ] 9.2 Mutations: `POST /api/docs`, `PUT /api/docs/{id}`, `DELETE /api/docs/{id}`, `POST /api/docs/{id}/annotate`, `/promote`, `/relation`
- [ ] 9.3 Policy: `PUT /api/docs/policy`, `PUT /api/docs/offer` (full replacement)
- [ ] 9.4 `POST /api/docs/reindex`
- [ ] 9.5 Every mutation via `DocsService` → external target = 409 `doc_read_only`; managed = audit row with actor
- [ ] 9.6 Dashboard: topic tree with ownership badge (`Managed`/`External`), origin badge (`Ancestor`), kind chip, plan progress, spec approval, section TOC, editor for managed, read-only for external, stale-refs panel, audit drawer (managed only)
- [ ] 9.7 `pnpm --filter eling-dashboard exec tsc --noEmit` passes; **no `pnpm build` while dev server runs on 4427**

---

## Definition of Done (per phase)

- [ ] All unit tests pass (`dotnet test --filter PhaseX`)
- [ ] `dotnet build --tl` passes with no new warnings
- [ ] No `TODO` or `throw new NotImplementedException()` left in the phase's files
- [ ] Commit message follows conventional format with phase scope

---

## Final Verification (after Phase 9)

- [ ] Full test suite: `dotnet test`
- [ ] Build: `dotnet build --tl` — no warnings
- [ ] Dashboard type-check: `pnpm --filter eling-dashboard exec tsc --noEmit`
- [ ] Integration smoke: create managed spec → promote → verify external in `docs/` → search finds it → annotate refused → audit trail exists
- [ ] Offer policy: decline → write 5 specs → offer reappears → accept → no more offers
- [ ] `decision: disabled` → verify zero `.eling/docs/` footprint, shard used, `docs/**` still indexed
- [ ] Commit spec + comparison + plan together: `git add docs/superpowers/specs/2026-11-20-docs-management-design.md docs/artifact-subsystems.md docs/superpowers/plans/2026-11-20-docs-management-implementation.md && git commit -m "docs: add docs management spec, subsystem comparison, and implementation plan"`