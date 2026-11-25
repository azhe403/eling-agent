# OpenCode External Skills — Technical Design Spec

Date: 2026-11-24
Status: Draft for review
Ownership: Managed (Eling-owned doc under `.eling/docs/`) — chosen by the user 2026-11-24.
Scope: `Eling.Core` (Skill path model, index schema, scanner) · `Eling.Backend` (DI, catalog watcher, MCP tool DTOs) · `Eling.Dashboard` (Skills page source badge/filter).

This is an amendment to the canonical skill-management spec `docs/superpowers/specs/2026-10-22-skill-management-design.md`. Where they conflict, this document wins for the OpenCode source only; every other behavior stays as in the canonical spec.

---

## 1. Intent and why

Skills reach the agent from two independent folders on this machine. OpenCode loads `~/.config/opencode/skills/{name}/SKILL.md` into the agent's available skills; Eling indexes only `~/.agents/skills/{name}/SKILL.md`. A concrete consequence: `dotnet-publish-staging` exists only under OpenCode's folder, so the agent can use it but `skill_get`, `skill_list`, `skill_search`, `recall`'s skills block, and the `/dashboard/skills` page are all blind to it. `~/.config/opencode` is even listed in `ElingPaths.DefaultExcludedRoots`, so it is deliberately never seen as plain code either.

The user asked for external skills with `source: opencode`. The goal is to make the OpenCode folder a first-class **read-only external source**: those skills become `origin = 'external'` rows carrying provenance `source.upstream = "opencode"`, visible everywhere Eling surfaces skills, with the OpenCode folder remaining the single source of truth. No copying, no duplicate catalog, no writes into OpenCode's files.

---

## 2. Goals, non-goals, anti-goals

Goals:

1. Discover and index `~/.config/opencode/skills/` as `origin = 'external'` with `source.upstream = "opencode"` and `source.tracked = true`.
2. Surface these rows in `skill_list`, `skill_search`, `skill_get`, `recall`'s skills block, and the Skills dashboard page, with the source visible to the user.
3. Keep OpenCode's folder authoritative and live: rescan and file changes refresh the index and the dashboard over SSE.
4. Never write, rename, or delete a file under `~/.config/opencode/skills/`.
5. Give explicit, safe answers for name collisions, delete, and adoption when the source is OpenCode.
6. Keep every OpenCode skill visible even when its name already exists in the catalog, by giving the indexed row a deterministic namespaced name.

Non-goals:

- No second writable catalog and no sync/import button. Snapshot import was considered and rejected (see §4).
- No mutation of OpenCode config, no skill enable/disable on the OpenCode side, no loading or unloading of skills for that agent.
- No change to `~/.agents/skills` as the Eling-managed catalog, and no new scope tiers (skills stay machine-global).
- No other external agent targets in v1 (Claude, OpenChamber, etc.). The mechanism should be data-driven so more sources can be added later without redesign.

Anti-goals (things that would make this a failure even if it "works"):

- A second copy of `dotnet-publish-staging` under `~/.agents/skills` that can drift from OpenCode's original.
- Eling writing frontmatter (`id`, `createdAt`, `updatedAt`, `source`) into a SKILL.md owned by OpenCode.
- An OpenCode skill silently overwriting or shadowing an existing Eling-managed skill of the same name.

---

## 3. Constraints verified in the current code

- `SkillCatalogPaths.CatalogDirectory` resolves to `~/.agents` (override `ELING_SKILLS_DIR`); `SkillsDirectory`, `TrashDirectory`, and `SkillFolder(name)` are all derived from it. `SkillCatalogScanner` takes one `catalogRoot` in its constructor; `FileSystemSkillStorage` and `SkillCatalogDirWatcher` also assume that single root.
- The `skills` table has `name TEXT NOT NULL UNIQUE`, `body_path TEXT NOT NULL` (documented as relative to `~/.agents`), `origin`, `trashed_at`, timestamps — and **no source columns**. `Skill.Source` exists as `SkillSource(upstream, tracked)`, but `SqliteSkillIndex`'s row mapper always passes `source: null`, so provenance is currently lost on read. Adding a column therefore also means fixing that round-trip to expose `source` in DTOs.
- Existing ownership invariant: `external` rows are read-only except delete-to-trash and `skill_adopt`. Both carve-outs are unsafe for an OpenCode-owned file: trash would move the original out of OpenCode's folder, and adopt rewrites frontmatter in place.
- The scanner already handles non-kebab folder names by taking the display name from frontmatter and keeping the real location in `body_path` (`TryRelaxFolderNameAsync`). OpenCode has such folders (e.g. `_skills__skills__engineering__tdd`), so this path must be reused rather than reinvented.
- Real name collisions exist between the two folders (`code-review`, `grilling`, `git-guardrails-claude-code`, `wizard`, `setup-pre-commit`, among others). Because the index has `UNIQUE(name)`, both cannot be stored under the same name; the OpenCode row gets a namespaced index name instead.
- Repo workflow constraints: the pre-commit hook runs the full backend suite; use `ttl_seconds: 1800` for commit leases. With the 4427 frontend dev server running, verify TypeScript with `pnpm --filter eling-dashboard exec tsc --noEmit`, never `pnpm build`. Backend under `dotnet watch` must be built to a separate output directory.

---

## 4. Approaches considered

| # | Approach | Tradeoff | Verdict |
| :-- | :-- | :-- | :-- |
| A | **Second scan root into the same index.** Add a registered external source for OpenCode; index its folders as `external` with provenance and source-qualified paths. Reuse one scan pass, one watcher family, one set of MCP tools and one dashboard page. | Deepest change: path resolution, index schema, scanner, watcher, DTOs, UI. | **Picked.** Keeps OpenCode authoritative and live with no duplication. |
| B | **One-way import/snapshot.** A tool plus dashboard action copies OpenCode SKILL.md files into `~/.agents/skills/`. | Smallest diff; reuses the single-root machinery as-is. But the result goes stale the moment OpenCode edits a skill, needs re-import, and leaves two copies that can drift. | Rejected — drift is exactly the problem being solved. |
| C | **Repoint the catalog root at OpenCode's folder.** | Would make Eling write into OpenCode's tree and remove `~/.agents` skills from the catalog. | Rejected — destroys the existing catalog and the ownership boundary. |
| D | **UI-only overlay** that reads OpenCode's folder live and merges it into the dashboard list. | No index or tool changes. But `skill_search`, `skill_get`, and `recall` stay blind, so agent-side recall cannot find these skills. | Rejected — breaks parity between dashboard and MCP surface. |

---

## 5. Design

### 5.1 Source descriptors and paths

Introduce a small immutable descriptor for an external skill source (key, display label, canonical root resolver, upstream string, tracked flag). The first descriptor is `opencode`, rooted at `Path.Combine(userHome, ".config", "opencode", "skills")`, with an `ELING_OPENCODE_SKILLS_DIR` environment override for test hosts, mirroring the existing `ELING_SKILLS_DIR` pattern. No machine-specific path is hardcoded; no personal usernames in code or tests.

Keep `SkillSource(upstream, tracked)` as the public provenance shape. The Eling catalog root keeps an empty upstream string — absence of provenance means "the canonical catalog". The OpenCode source uses `upstream = "opencode"`, `tracked = true`.

### 5.2 Index schema and provenance

Add two idempotent columns to `skills`: `source_upstream TEXT NOT NULL DEFAULT ''` and `source_tracked INTEGER NOT NULL DEFAULT 0`. They are metadata only, not FTS-indexed; the FTS5 external-content table stays unchanged. Add a forward-only, idempotent migration for existing `skill-index.db` files (a fresh open currently creates the schema lazily; a legacy DB must gain the columns without data loss). Do not rewrite the canonical SKILL.md of any origin to store this.

`body_path` stays relative, but is now resolved against a base chosen by `source_upstream`: empty means `SkillCatalogPaths.SkillsDirectory`; `"opencode"` means the OpenCode root. For an OpenCode folder whose name is not kebab-case, keep the real relative location in `body_path` (e.g. `<folder>/SKILL.md`) exactly as the relaxed external indexing already does, and use the frontmatter `name` as the display identity. Centralize this in one path resolver used by read, list-paths, missing-detection, delete, adopt, and restore — no caller may do `Path.Combine(catalogRoot, bodyPath)` itself.

For a namespaced row (§5.3), the index `name` is the namespaced form while `body_path` still points at the original OpenCode folder; this mirrors the existing relaxed external indexing, where the display name and the real filesystem location are deliberately separate. All by-name lookups (`skill_get`, `skill_delete`, `skill_adopt`) use the index name.

Fix the `SqliteSkillIndex` row mapper to populate `Skill.Source` from the new columns so provenance survives a round-trip, and expose `source` on the read DTOs returned by `skill_get`/`skill_list`/`skill_search`. **Call (builder need not re-decide):** columns holding `''`/`0` map to `source: null`, so an unsourced catalog row keeps today's wire contract and does not get a source badge; only a sourced row emits `{ upstream, tracked }`.

### 5.3 Scanner and collision policy

Generalize the scan to walk an ordered list of roots in deterministic priority order: Eling catalog first, then OpenCode. Keep `SkillCatalogScanner`'s existing single-root constructor/behavior as the default so current DI and tests do not break; add an overload that accepts the source list.

**Collision rule (user decision 2026-11-24 — both skills stay visible):** no folder is dropped just because its name is taken. Maintain a global set of claimed names, seeded from the catalog root (active and trashed rows, any origin). For each OpenCode folder, take the validated frontmatter/folder name as the base; if that name is claimed, append `-opencode`; if that is claimed too, append `-opencode-2`, `-opencode-3`, and so on in deterministic ordinal order until free. The resulting index name must still be valid kebab-case and within the 128-character limit; if not, record a `SkillScanFailure` instead of creating a row. The original frontmatter `name` remains the human display name on the card, with the source badge making the origin explicit. Never overwrite a row from another root — namespacing is the only collision resolution. All by-name MCP lookups use the index name, so `code-review-opencode` is how an agent fetches the OpenCode copy while `code-review` still fetches the catalog copy.

Missing detection must resolve `body_path` via the same source-aware resolver, and must skip sources whose root directory is absent (an unmounted OpenCode folder is not evidence that its skills were deleted).

### 5.4 Read-only, delete, and adoption

- All existing mutating service paths continue to refuse `origin = 'external'`.
- `skill_delete` on an OpenCode-source row is **refused** with a clear message; the file belongs to OpenCode and a trash move would remove the original. Unsourced external rows in `~/.agents` keep today's delete-to-trash behavior.
- `skill_adopt` on an OpenCode-source row does **not** rewrite the OpenCode file. Instead it copies the skill folder into `~/.agents/skills/{indexName}/`, writes Eling frontmatter there, flips that new row to `origin = 'eling'` while preserving `source.upstream = "opencode"` for provenance, and leaves the OpenCode row on disk. The existing OpenCode row is removed from the index by the adopt; on the next scan it reappears as external and, because the adopted catalog row now holds that name, receives a namespaced name under §5.3. The user-facing result is one editable Eling skill and an untouched original, both still visible. `skill_adopt` for unsourced external skills keeps its existing in-place semantics.

### 5.5 Watcher and realtime

Extend `SkillCatalogDirWatcher` (rather than adding a sixth near-identical watcher service) to watch all registered roots and publish through the existing `SkillChangeTopics` SSE path, so the dashboard refreshes when OpenCode's folder changes. **Call:** Eling never creates the OpenCode root — no `Directory.CreateDirectory` on that path, because that would mutate OpenCode's config tree against a non-goal. A root absent at startup is simply registered as not-yet-present and discovered on the next scan (startup/manual/catalog-watcher-triggered); there is no periodic root-existence timer in v1.

### 5.6 MCP and dashboard surface

No new MCP tool is needed; ToolCatalog stays unchanged. `skill_list`, `skill_search`, `skill_get`, and the recall skills block return the source so an agent can tell where a skill lives. The Skills page shows a source badge (e.g. `opencode`) on sourced rows and offers a source filter (`all` / `eling catalog` / `opencode`) following the repo's master-detail layout conventions. **Call:** source is a SECOND, independent filter dimension alongside the existing `origin = all/eling/external` filter — add a separate query parameter and control rather than replacing origin; the two are orthogonal (an OpenCode skill is `external`, and so are many catalog skills). Sourced-external rows make edit actions visibly unavailable with the reason from §5.4. Add the source badge/filter to the existing card and detail-panel components and their tests. **Call:** folders that fail to index (unparseable frontmatter, missing SKILL.md, invalid namespaced name) are reported through the existing rescan failure list; the Skills page itself shows only indexed rows.

### 5.7 Tests

Cover: second-root discovery including a non-kebab folder; provenance written and read back (and `null` for catalog rows); collision with both an Eling-owned and an unsourced catalog row producing `-opencode`/`-opencode-2` names while the original stays visible; display name vs index name; absent OpenCode root not causing false "missing" removals and never being created; schema migration on a legacy `skill-index.db`; read-only and delete-refusal guards; adopt copy-and-shadow leaving the original untouched; watcher/SSE refresh for the new root; dashboard source badge, independent origin/source filters, and rescan-failure reporting.

---

## 6. Decisions made here (do not re-litigate during planning)

- Two roots in one index, not import/snapshot and not a second catalog (§4).
- `source.upstream = "opencode"`, `source.tracked = true`; provenance is index-only metadata and is persisted in new columns (§5.1–5.2).
- Both skills stay visible on a name collision: OpenCode rows receive a deterministic `-opencode`, `-opencode-2`, … index name; the original display name and provenance survive (§5.3).
- Unsourced rows serialize as `source: null`; source is a separate filter from origin; Eling never creates the OpenCode root and has no periodic root timer; unindexable folders surface in the rescan failure list only (§5.2, §5.5–5.6).
- Delete is refused for OpenCode-source rows; adoption copies rather than rewriting the original (§5.4).
- One watcher service watches all roots; no new background service (§5.5).
- No new MCP tool; source is exposed on existing read surfaces (§5.6).
- Design doc is Eling-managed under `.eling/docs/` per the user's explicit choice.

## 7. Left to the builder

Exact class and method names, DI registration sites, the migration implementation, concrete file paths for tests, and the precise UI component/call-site changes are builder decisions. Keep them consistent with the existing skill subsystem, the repo's convention tests, and the house spec. If a detail here turns out to be impossible without changing one of the §6 decisions, stop and report it rather than working around it.
