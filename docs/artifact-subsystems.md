# Eling Artifact Subsystems — Comparison

> **Last verified:** 2026-11-20
>
> Living document. Update it whenever a subsystem changes shape, is implemented, or is superseded.
> It records *why* the boundaries fall where they do, so the same territory is not re-argued — and
> the same mistake is not repeated — by a later session.

Eling stores five kinds of artefact. They look similar at a glance (all are files, all have
front-matter, all are searchable) and they are deliberately not one subsystem. What separates them
is not file size: it is **what the artefact is for, and what a reader does with it**.

---

## 1. At a glance

| | **Memory** | **Intention** | **Skill** | **Snippet** | **Docs** |
|---|---|---|---|---|---|
| Status | implemented | implemented | implemented | **not implemented** | **implemented** (backend + dashboard) |
| Code | `Eling.Core/Memory/` | `Eling.Core/Intention/` | `Eling.Core/Skill/` (42 files) | none (`Snippet/` absent) | `Eling.Core/Docs/` + `Eling.Backend/Endpoints/DocEndpoints.cs` + `Eling.Dashboard/src/app/dashboard/docs/` |
| What it is | one fact | a future trigger | a procedure | one code block | a structured document |
| Typical size | ~700 B | ~300 B | ~5 KB | ~1 KB | 2–40 KB |
| Sections | no | no | no | no | **yes** |
| Storage | `.eling/memories/*.md` | `.eling/intentions/*.md` | `.agents/skills/<name>/SKILL.md` | — | `.eling/docs/<topic>/<slug>.md` + `docs/` |
| Index | `index.db` | same db as memory | `skill-index.db` | — | `docs-index.db` |
| Front-matter | required | required | required | pattern borrowed from Skill | **optional** |
| Scope tiers | local / project / global | same as memory | global only | project + global | managed / external × current / ancestor / shard / global |
| Ownership marker | none (Eling owns all) | none | `SkillOrigin.Eling \| External` | planned | `DocOwnership.Managed \| External` |
| Search axis | FTS + tags | trigger match | FTS + tags + origin | **FTS + `language`** + tags | FTS + kind + topic + status + ownership |
| Unit of replacement | whole note | whole intention | whole skill | whole snippet | **per section** |
| Lifecycle | active → superseded → archived | active → expired / superseded | active → trashed → restored | active → superseded | per-kind status + supersession |
| Relations | to memories, snippets | — | to memories, snippets | to memories | to other docs (spec ↔ plan) |
| Written by | agent | agent | agent (`eling` origin only) | agent | agent (`managed` only), **or a human via REST** |
| Reader | agent, at recall | agent, on trigger | agent, to follow a procedure | agent, to paste code | **human reviewing, agent executing** |
| Editable over REST | yes | yes | yes (`eling` origin only) | — | yes (`managed` only) |

---

## 2. The axes that actually separate them

Five questions decide every boundary below. Answering them in this order keeps the reasoning
consistent.

### 2.1 Does it have internal structure?

The single sharpest divider.

- **No structure** — Memory, Intention, Snippet: one block, replaced whole. Recovery is
  supersede-the-whole-thing.
- **Structure** — Docs: many sections that age independently. Hence per-section anchors,
  per-section annotation, and section-level retrieval.

This is why Docs cannot be a Memory kind. A 34 KB spec typically supersedes *sections* of four
other specs; a memory's only replacement mechanism is the whole note.

### 2.2 Does it carry execution or approval state?

- **Intention** carries a *trigger* and an *expiry* — state about **when to surface it**.
- **Docs/plan** carries *step progress* — state about **how far the work has gone**.
- **Docs/spec** carries *approval status* — state about **whether it may be implemented**.
- Memory, Skill, Snippet carry none of these.

A plan's `- [ ]` / `- [x]` checkboxes are the clearest example: they are not prose and not
retrieval hints, they are dashboard data. Folding them into Memory would put step state on a small
note, and both models would be wrong.

### 2.3 Is there a mandatory technical dimension?

- **Snippet** requires `language`, and searches filter by it.
- Everything else has no such axis.

This is what keeps Snippet separate even from Docs. Docs has no concept of "this section is written
in C#". Merging them would produce a `language` column null for every other kind, a section chunker
that does nothing on a single code block, and a search filter valid for one kind only.

### 2.4 Who owns the file?

Two levels appear in Eling, and they are not the same mechanism:

- **Skill** — `SkillOrigin.Eling | External`, index-only, never written to front-matter. External
  skills are "permanently read-only to every mutating path". A catalog assembled from third-party
  tooling.
- **Docs** — `DocOwnership.Managed | External`, same shape. `docs/` in a repository is **external**
  even though it sits in the workspace the user opened: location does not confer ownership.

Memory, Intention, and Snippet have no ownership axis because Eling is the only writer.

### 2.5 What does the reader do with it?

- Memory → **recalled**: injected into context as one fact.
- Intention → **triggered**: surfaced when context matches.
- Skill → **followed**: read as a procedure, step by step.
- Snippet → **pasted**: copied as working code.
- Docs → **reviewed and executed**: a human judges a design, an agent implements a plan.

The reader's job is what makes Docs the only subsystem with a human as a first-class audience.

---

## 3. Pairwise boundaries, and why each is not a merge

### 3.1 Memory ↔ Intention

Different time direction. A memory is true *now*; an intention is true *later*, conditioned on a
trigger (`Topic`, `FilePattern`, `TimeBased`) and optionally expiring. Same storage shape, same
front-matter, same `MemoryStatus` enum — but one is retrieved, the other is armed.

### 3.2 Memory ↔ Skill

Different unit of reuse. A memory is recalled and discarded from context; a skill is loaded and
*executed* as instructions. Skills also carry a machine-global scope (`~/.agents/skills/`) that is
outside every project, because a procedure outlives the repository that spawned it.

### 3.3 Skill ↔ Docs

**The thinnest boundary in the system.** Roughly eleven of the twelve concepts Docs needs already
exist in Skill, down to the same dual-FTS tokenizer pair. Full detail in
[the Docs spec §1.5](superpowers/specs/2026-11-20-docs-management-design.md).

Only four things are genuinely unique to Docs, and all come from §2.1: section chunks, supersession,
plan progress, staleness resolution.

They stay separate because the *unit* differs — a skill is one procedure with a name, a doc is a
document with sections — not because the infrastructure differs. Consolidation of the shared
machinery is deliberate later work.

One asymmetry is correct rather than inconsistent:
`skill_adopt` moves **External → Eling** (a skill catalog is assembled from third-party tooling),
while `doc_save(promote: true)` moves **Managed → External** (a document is released to the
repository so it survives as project documentation). The direction follows who the artefact
belongs to by design.

### 3.4 Snippet ↔ Skill and Snippet ↔ Docs

Snippet and Skill are the closest pair that *should* end up adjacent: both are code-bearing. The
difference is that a skill is a *procedure* (its body is instructions, its sibling files are
scripts) while a snippet is a *fragment* (its body is code, addressable by `language`).

Snippet stays out of Docs for the `language` reason in §2.3.

### 3.5 Docs ↔ Memory

The confusion this document exists to prevent. Settled:
**Docs is never `MemoryKind.Doc`, and Memory never grows a `kind` field.** They share helpers and
patterns, never a table or a tool.

---

## 4. Shared machinery (and where it is duplicated)

| Concern | Implementation | Shared? |
|---|---|---|
| Markdown + YAML front-matter parse | Memory, Intention, Skill each have their own parser | duplicated three times already |
| Dual FTS5 (porter `unicode61` + trigram) | Memory `index.db`, Skill `skill-index.db` | duplicated; Docs would be the third |
| Idempotent catalog scanner | `SkillCatalogScanner` | Skill only so far |
| Index-as-rebuildable-cache | all | convention, not code |
| Scope resolution | `Scope/` — `ResolveCanonicalProjectRoot`, `ResolveProjectLocalDir` | **genuinely shared** |
| Glob matching | `Matching/GlobPattern` | **genuinely shared** (intention triggers + scope policy) |
| Live refresh | `ISkillChangeNotifier` | Skill only so far |

The shared pieces are the two in bold. Everything above them is a copy. That is a known cost, not
an accident: extracting a common `MarkdownCatalog` layer would touch working, tested subsystems, so
it is sequenced *after* the artefacts themselves exist — never before.

---

## 5. Decision log

| Decision | Rationale |
|---|---|
| Intention is separate from Memory despite sharing the shape | Different time direction and trigger semantics (§3.1) |
| Skill is machine-global, not project-scoped | A procedure outlives the repository that spawned it (§3.2) |
| Docs is a separate subsystem, not `MemoryKind.Doc` | Docs has sections that age independently, and carries progress/approval state (§2.1, §2.2) |
| Docs does not include Snippet as a kind | Snippet has a mandatory `language` axis Docs has no use for (§2.3) |
| Docs copies Skill's pattern instead of extracting a shared layer | Refactoring 42 files of working Skill code for a subsystem that does not exist yet is the worse trade |
| `docs/` in a repo is `external`, not managed | Location does not confer ownership; the repository owns it |
| `skill_adopt` and `doc_save(promote)` point opposite ways | Each direction follows who the artefact belongs to |
| Mutation is a service rule, not a transport rule | MCP and REST both call the same service, so ownership and audit hold on both paths and a future desktop client inherits them for free |

---

## 6. See also

- [architecture.md](architecture.md) — layer map and feature index
- [nested-memory.md](nested-memory.md) — scope tiers and ancestor resolution
- [recall-architecture.md](recall-architecture.md) — hybrid FTS + RRF, overflow summaries
- [superpowers/specs/2026-11-20-docs-management-design.md](superpowers/specs/2026-11-20-docs-management-design.md) — Docs design
- [superpowers/specs/2026-09-18-snippet-management-design.md](superpowers/specs/2026-09-18-snippet-management-design.md) — Snippet design (unimplemented)
- [superpowers/specs/2026-10-22-skill-management-design.md](superpowers/specs/2026-10-22-skill-management-design.md) — Skill design (implemented)
