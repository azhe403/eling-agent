# Project Scope Policy (Machine-Local Opt-Out) — Design

**Date:** 2026-09-11
**Status:** Implemented (rev 4 decisions locked; implementation landed — see §14)
**Scope:** `Eling.Core` (pure policy model + glob matching), `Eling.Backend` (policy store, MCP tool, recall/status/save wiring), tests in `Eling.Core.Tests` + `Eling.Backend.Tests`
**Related:** `docs/superpowers/specs/2026-09-09-scope-chain-adoption-design.md` (consent-gated init), `docs/recall-architecture.md`

> All project names and paths in this document are anonymized placeholders. At runtime,
> policy keys are the platform-normalized absolute project root; `~/...` is used here for
> readability only. No real usernames or machine paths appear in this document, per the
> project hygiene rule.

---

## 1. Goal

Give the user a way to say **"never use project-scope memory here"** for the current
project, a set of paths, or by default — and have Eling honor it **silently and
persistently**, so the agent stops offering onboarding and project-scoped writes stop
failing with `init-required`.

Today, an uninitialized project reports `adoptable` and a default `memory_save`
(`scope=project`) returns `init-required`, which prompts the agent to ask for consent on
every session and on every save attempt. There is no way to record "no, and don't ask
again". This design adds that record.

The decision is stored **machine-locally** under the user scope
(`~/.config/eling/config/project-policy.json`). It intentionally does **not** travel with
the repo: it is a personal, per-machine preference, consistent with the rule that project
files must not carry machine-specific state. Team-wide or repo-portable opt-out is
explicitly out of scope (§10).

Disabling is a **reversible toggle, never a lock**: the user can re-enable project scope at
any time, for a single project, a path rule, or globally (§6.4).

The document **and each entry** record when they were first decided and last changed
(`createdAt` / `updatedAt`, ISO 8601 UTC, §4.4), so a decision is not just a fact but a
dated, inspectable one.

---

## 2. Non-Goals (YAGNI)

- No repo-committed marker (`.elingignore`, config key) — that would add a file to a repo
  the user is trying to avoid touching, and would make a personal choice a team decision.
- No dashboard/HTTP parity in this pass (follow-up, consistent with prior specs).
- No pattern expiry, time-boxed re-offer, or "ask again after N days" logic (now feasible —
  entries carry dates — but still deferred as a behavior).
- No per-memory or per-tag policy — the unit of policy is a **project root / path pattern**.
- No allowlist/denylist engine beyond the three decision inputs described in §5.
- No decision history / audit log beyond the current per-entry dates (§4.4).
- No change to the user-home guard, the scope chain model, or global-scope resolution.

---

## 3. Design summary

A small JSON policy file lives in the user scope's config directory:

```
<user-scope>/config/project-policy.json        # ~/.config/eling/config/project-policy.json
```

It carries an ordered set of decisions resolved by specificity:

```
exact project root  →  longest matching path glob  →  default
```

Each decision is one of:

| Decision   | Meaning |
|------------|---------|
| `ask`      | Current behavior: when the project is uninitialized, `adoptable` is true and the agent may offer consent-gated onboarding. |
| `disabled` | Project scope is **off** for this workspace: onboarding is never offered, and project-targeted operations resolve to global (§6). |

The policy is evaluated **fresh on every call** (directory checks + a small config read),
never cached from process start, so a decision takes effect immediately without restarting
the host. The document and each entry carry `createdAt` / `updatedAt` timestamps (§4.4) so
the user can see when a decision was first made and last changed; timestamps never affect
resolution.

Three surfaces consume the policy:

1. **Onboarding signal** — `memory_recall` reports it in a new `projectScope` block; the
   agent sees `adoptable` and `policy` at session start without being nagged.
2. **Status** — `memory_project_status` reflects the same resolved decision, plus the
   applicable entry's dates.
3. **Writes** — `memory_save` consults the policy before returning `init-required`.

A new tool, `memory_project_scope_policy`, writes and clears decisions, so disabling and
re-enabling are both explicit, user-driven actions (§6.4).

The user scope has **no consent gate** (unlike a project `.eling`): `UserScope.Resolve`
always points at `~/.config/eling/` and its `runtime/` directory is auto-created. Storing
the policy there requires no init ceremony and never touches the project.

---

## 4. Data model

### 4.1 File schema

`<user-scope>/config/project-policy.json`:

```json
{
  "version": 1,
  "createdAt": "2026-09-11T09:15:00Z",
  "updatedAt": "2026-09-11T12:30:00Z",
  "default": "ask",
  "projects": {
    "~/work/acme/legacy-app": {
      "decision": "disabled",
      "createdAt": "2026-09-11T12:30:00Z",
      "updatedAt": "2026-09-11T12:30:00Z"
    }
  },
  "patterns": [
    {
      "glob": "~/work/acme/legacy/**",
      "decision": "disabled",
      "createdAt": "2026-09-11T10:00:00Z",
      "updatedAt": "2026-09-11T12:30:00Z"
    },
    {
      "glob": "~/sandbox/**",
      "decision": "ask",
      "createdAt": "2026-09-11T10:05:00Z",
      "updatedAt": "2026-09-11T10:05:00Z"
    }
  ]
}
```

| Field | Type | Default | Notes |
|---|---|---|---|
| `version` | int | `1` | Schema version for forward migration. |
| `createdAt` | string (ISO 8601 UTC) \| null | `null` | First effective write of the document; never changes afterward (§4.4). |
| `updatedAt` | string (ISO 8601 UTC) \| null | `null` | Last write that **actually changed** the effective policy (§4.4). |
| `default` | `"ask"` \| `"disabled"` | `"ask"` | Decision when nothing more specific matches. |
| `projects` | map<root, entry> | `{}` | Exact project roots. Entry = `{ decision, createdAt, updatedAt }` (§4.4). Keys are normalized absolute paths. |
| `patterns` | array<{glob, decision, createdAt, updatedAt}> | `[]` | Ordered glob rules; most specific match wins (§5). |

A missing file is equivalent to
`{ "version": 1, "createdAt": null, "updatedAt": null, "default": "ask", "projects": {}, "patterns": [] }`.
A corrupt or unreadable file is treated the same as missing, and a warning is logged —
never a crash, never an overwrite.

### 4.2 Decision values

Stored decision strings are `ask` and `disabled` (lowercase). Unknown strings are rejected
at the tool boundary and ignored (treated as `ask`) when read from a hand-edited file.
`clear` (§8) is a write-time directive only; it is never stored.

### 4.3 Normalization

- Paths are made absolute (`Path.GetFullPath`) and separators are normalized to `/`.
- Matching is **case-insensitive**, consistent with the existing `ScopeChain.HasOwnScope`
  comparison.
- Glob syntax reuses the existing matcher (`**`, `*`, `?`). The current implementation in
  `Eling.Core.MemoryRecall.GlobPattern` is `internal`; it is promoted to a shared public
  helper so both tag/path matching and policy matching use one implementation.

### 4.4 Timestamps

One vocabulary at two levels — document and entry — with identical semantics:

- **Format:** ISO 8601 UTC, round-trip (`Z` suffix), from `DateTimeOffset.UtcNow`. The
  store takes an injectable clock so tests are deterministic (§7.3).
- **`createdAt` = the first decision.** For the document: the first successful, effective
  write ever. For an entry: the first time that entry's decision was recorded. In both
  cases it is **immutable** — later changes never touch it.
- **`updatedAt` = the last effective change.** For the document: when any decision
  (`default`, any entry, any `clear` that removed something) last changed. For an entry:
  when that entry's decision **value** last changed. On creation, entry `createdAt ==
  updatedAt`.
- **Idempotent writes change nothing.** Re-setting the same decision value leaves the
  entry's dates and the document's `updatedAt` untouched — "when was this changed" stays
  meaningful instead of drifting to "when was the file last touched".
- **`default` stays a plain string.** Its changes are captured by the document's
  `updatedAt`; entries get the full pair because they are the enumerable, individually
  meaningful decisions.
- **`clear` removes the entry with its dates.** v1 keeps no history (§2).
- A hand-edited file that omits timestamps loads them as `null`; the next tool write
  backfills them (document pair immediately; an entry's pair the next time that entry is
  written).
- Timestamps are informational: resolution (§5) ignores them.

---

## 5. Resolution algorithm

Given the workspace `cwd`:

1. Normalize `cwd` to a root candidate (see §4.3).
2. If `cwd` is the user home → decision is irrelevant (user-home is never adoptable). Stop.
3. If `projects[root]` exists → return its `decision`.
4. Else, among `patterns[]` whose `glob` matches `root`, pick the **longest `glob` string**
   (most specific) and return its `decision`. Ties break by array order.
5. Else return `default`.

The result is a pure value; the caller combines it with the scope chain to produce the
public posture (§6). Timestamps are not consulted.

Specificity rationale: exact keys beat patterns, and a longer glob (e.g.
`~/work/acme/legacy/**`) beats a shorter one (`~/work/**`). This makes targeted overrides
predictable without a priority field.

---

## 6. Semantics

### 6.1 Public signal

`projectScope` block (added to `memory_recall`, mirrored by `memory_project_status`):

```json
{
  "posture": "uninitialized",
  "policy": "ask",
  "adoptable": true,
  "initialized": false,
  "headRoot": null
}
```

Rules:

- `posture` is the existing scope-chain fact: `own-scope` | `ancestor-scope` |
  `uninitialized` | `user-home`.
- `policy` is the resolved decision: `ask` | `disabled`.
- `adoptable` = the workspace is **not the user home**, has **no own scope**, and
  `policy == "ask"`. This matches `memory_project_status`'s long-standing meaning ("this
  workspace can adopt its own `.eling`") and therefore also covers an `ancestor-scope`
  workspace. A `disabled` workspace is never adoptable, even when it is otherwise eligible.
- `initialized` / `headRoot` are unchanged facts.
- Timestamps are intentionally **not** surfaced here (recall is high-frequency and should
  stay lean); they are returned by the policy tool (§8) and `memory_project_status` (§9.2).

### 6.2 `disabled` = project scope off

When `policy == "disabled"` for the workspace, the workspace behaves as if it has **no
project scope**:

- **Onboarding:** never offered. `adoptable` is `false`; the tool never returns
  `init-required` for that workspace.
- **Writes:** `memory_save` with `scope=project`, `scope=auto`, or the default resolves to
  **global**. The write succeeds against global storage and the response makes the
  reroute explicit:
  - `scope` reports `"global"`,
  - a `projectScopeDisabled: true` flag is set,
  - a short `note` explains the reroute.
- **Reads:** default and `merged` resolution covers **global only** for that workspace.
  Explicit reference reads (`memory_get` by a full reference/ID to an existing ancestor
  memory) still work; the policy turns off *scope resolution*, not data access.
- **Init:** `memory_init_project` still blocks on consent, but the policy means the agent
  should not be offering it. Re-enabling is covered in §6.4.

This is the literal reading of "truly disable project scope"; it is a recorded design
decision (§12.1).

### 6.3 `ask` preserves today

With `policy == "ask"`, everything behaves exactly as it does now: `adoptable` when
uninitialized, `init-required` on a blocked project write, consent-gated init.

### 6.4 Re-enabling (disable is a toggle)

A `disabled` decision is a preference, not a lock. The user can restore normal project
scope at any time, at any granularity, without editing code or restarting the host:

| Re-enable scope | Action |
|---|---|
| One project | Set the workspace's decision back to `ask`, or `clear` its exact entry so it falls through. |
| One path rule | Set the `pattern` back to `ask`, or `clear` the pattern entry. |
| Everything | Set `default` back to `ask`. |

Two paths make this possible:

1. **Via the agent** — `memory_project_scope_policy(decision: "ask" | "clear", target: ...)`
   (§8). The agent must only do this when the user asks.
2. **By hand** — the file is plain JSON in the user scope, and `Load` reads it fresh on
   every evaluation, so editing `<user-scope>/config/project-policy.json` takes effect on
   the next call with no restart.

Interplay rules that make re-enabling precise:

- An **exact `ask` entry beats a broader `disabled` pattern** (exact wins in §5), so a
  single project under a disabled tree can be re-enabled without touching the pattern.
- `clear` removes the entry or pattern entirely, so resolution falls back to the next
  level (pattern, then `default`). Prefer `clear` when the intent is "forget my override"
  rather than "explicitly opt back in".
- Re-enabling restores the full `ask` behavior: `adoptable` returns for an uninitialized
  workspace and `init-required` returns for a blocked project write (§6.3).
- A value change (§4.4) moves the entry's `updatedAt` (and the document's), so the date a
  project was re-enabled is recorded while its original decision date stays in `createdAt`.
  This is the audit trail: "disabled sejak X, diaktifkan kembali tanggal Y".

One caveat, stated so it is not a surprise: while a workspace was disabled, its
project-targeted writes were rerouted to **global** (§6.2). Re-enabling does not migrate
that data back into project scope; those memories stay in global unless moved explicitly
with the existing promote/move operations.

---

## 7. Components

### 7.1 Core — pure policy model

Location: `src/backend/Eling.Core/Scope/`

```csharp
namespace Eling.Core.Scope;

public enum ProjectScopeDecision { Ask, Disabled }

/// <summary>One stored decision + its two-level dates (§4.4).</summary>
public sealed record ProjectScopeEntry(
    ProjectScopeDecision Decision,
    DateTimeOffset? CreatedAt,   // first decision; immutable
    DateTimeOffset? UpdatedAt);  // last effective change; == CreatedAt at creation

public sealed record ProjectScopePattern(
    string Glob,
    ProjectScopeEntry Entry);

public sealed record ProjectScopePolicy(
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    ProjectScopeDecision Default,
    IReadOnlyDictionary<string, ProjectScopeEntry> Projects,
    IReadOnlyList<ProjectScopePattern> Patterns)
{
    public static ProjectScopePolicy DefaultPolicy { get; } =
        new(null, null, ProjectScopeDecision.Ask,
            new Dictionary<string, ProjectScopeEntry>(), []);

    public ProjectScopeDecision Resolve(string projectRoot);   // §5, pure — dates ignored
}
```

Glob matching is exposed as a public helper (promoted from the existing
`internal GlobPattern`).

### 7.2 Core — glob helper promotion

Expose the existing matcher once, e.g. `Eling.Core.Matching.GlobPattern.IsMatch(glob, path)`,
and repoint the existing recall usage at it. One implementation, two callers.

### 7.3 Backend — policy store

Location: `src/backend/Eling.Backend/Scope/`

```csharp
public interface IProjectScopePolicyStore
{
    Task<ProjectScopePolicy> LoadAsync();                                        // missing/corrupt → DefaultPolicy + warning
    Task<ProjectScopePolicy> SetProjectAsync(string root, ProjectScopeDecision decision);   // upsert
    Task<ProjectScopePolicy> SetPatternAsync(string glob, ProjectScopeDecision decision);   // upsert
    Task<ProjectScopePolicy> SetDefaultAsync(ProjectScopeDecision decision);
    Task<ProjectScopePolicy> ClearProjectAsync(string root);                     // remove exact entry (§6.4)
    Task<ProjectScopePolicy> ClearPatternAsync(string glob);                     // remove pattern entry (§6.4)
}

public sealed class JsonProjectScopePolicyStore : IProjectScopePolicyStore
{
    public JsonProjectScopePolicyStore(
        UserScope userScope,
        Func<DateTimeOffset>? clock = null,
        string? userHomeDirectory = null,
        ILogger<JsonProjectScopePolicyStore>? logger = null);
    // Async file I/O to <UserScope.ConfigDirectory>/project-policy.json, matching the async storage engine
}
```

Rules:

- Every mutation loads → mutates → persists the whole document asynchronously. The write
  merges into the existing JSON, never drops keys it does not own (`version` preserved),
  never writes a partial file (temp + atomic move), and is idempotent.
- The store **owns all timestamps** (document and per-entry), applying the §4.4 rules
  exactly once, in one place: preserve `createdAt` (set on first effective write), move
  `updatedAt` only on effective change. Idempotent writes are no-ops (file not rewritten).
- File I/O is **fully async** (`File.ReadAllTextAsync` / `File.WriteAllTextAsync`), matching
  the async storage engine (`FileSystemMemoryStorage`, `SqliteMemoryIndex`); no
  `Task.FromResult` wrapper appears anywhere in this path.
- The clock is injectable (`Func<DateTimeOffset>`, default `() => DateTimeOffset.UtcNow`)
  so timestamp tests are deterministic.
- `LoadAsync` is called fresh per evaluation (cheap) so edits apply without a host restart.
- The store is injected as a singleton.

### 7.4 Backend — posture

`ProjectScopePosture.EvaluateAsync(cwd, userHome, policyStore)` (the shared helper introduced
for `memory_recall`/`memory_project_status`) awaits the policy store and computes
`policy`, `adoptable`, `posture`, `initialized`, `headRoot`. Dates are not part of the
posture value; consumers read them from the store when needed (§9.2).

### 7.5 Backend — new MCP tool

Location: `src/backend/Eling.Backend/Mcp/Tools/MemoryProjectScopePolicyTool.cs`

`[McpServerToolType]`, one method `memory_project_scope_policy` (contract in §8).

---

## 8. Tool contract — `memory_project_scope_policy`

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `decision` | string | yes | — | `ask`, `disabled`, or `clear`. `clear` removes the matching entry/pattern so resolution falls back to the next level. |
| `target` | string | no | `project` | `project` (this workspace) \| `pattern` \| `default`. |
| `pattern` | string | when `target=pattern` | — | Glob for the path rule, e.g. `~/work/acme/legacy/**`. |

Returns JSON:

```json
{
  "ok": true,
  "target": "project",
  "decision": "disabled",
  "appliedTo": "<normalized-project-root>",
  "policy": {
    "version": 1,
    "createdAt": "2026-09-11T09:15:00Z",
    "updatedAt": "2026-09-11T12:30:00Z",
    "default": "ask",
    "projects": {
      "<normalized-project-root>": {
        "decision": "disabled",
        "createdAt": "2026-09-11T12:30:00Z",
        "updatedAt": "2026-09-11T12:30:00Z"
      }
    },
    "patterns": []
  }
}
```

`clear` is a write directive, not a stored value: it deletes the exact `projects` entry or
the matching `patterns` entry (dates included) and never appears in the resulting policy.
Stored decisions remain `ask` / `disabled` only (§4.2). The payload echoes
`decision: "clear"` so the caller can confirm what happened. The echoed `policy` carries
the current dates; per §4.4, `updatedAt` values advance only when this call actually
changed something.

Error codes reuse the existing `{ ok: false, error, code }` convention:

| Code | When |
|---|---|
| `invalid_argument` | Missing/unknown `decision`; `target=pattern` without `pattern`; empty `pattern`. |
| `rejected_user_home` | `target=project` resolved to the user home. |
| `not_found` | `clear` targeted an entry or pattern that does not exist. |
| `internal_error` | The policy file could not be written. |

Authorization/consent: this tool mutates user configuration. The agent must only call it
after the user **explicitly** says to disable (or re-enable) project scope here. It is never
invoked automatically.

---

## 9. Affected surfaces

### 9.1 `memory_recall`

Adds the `projectScope` block (§6.1). Because the policy affects `adoptable`, the earlier
"expose `adoptable` in recall" work folds into this design rather than shipping separately.
Output shape:

```json
"projectScope": {
  "posture": "uninitialized",
  "policy": "disabled",
  "adoptable": false,
  "initialized": false,
  "headRoot": null
}
```

### 9.2 `memory_project_status`

Returns the same `policy` + `adoptable` resolution so status and recall never disagree,
plus inspection fields: the document's `createdAt` / `updatedAt` and the **applicable
entry's** `createdAt` / `updatedAt` (which rule won, and when it was decided/changed);
`null` when the resolution fell through to `default`.

### 9.3 `memory_save`

On an uninitialized project:

- `policy == "ask"` → unchanged: returns `init-required`.
- `policy == "disabled"` → routes to global, returns a successful save with
  `scope: "global"` and `projectScopeDisabled: true` (§6.2). No `init-required`.

### 9.4 `ServerInstructions`

Replace the "when `memory_project_status` reports `adoptable` …" sentence with policy-aware
wording, e.g.:

> Project scope initialization requires user consent. Offer it at most once per session,
> and only when `memory_recall` reports `projectScope.adoptable: true`. If a default
> `memory_save` returns `init-required`, relay it once. When `projectScope.policy` is
> `disabled`, do not offer initialization — use `scope=global`. The backend never creates
> `.eling` on its own. If the user asks to enable project memory again for a disabled
> workspace, set the policy back to `ask` (or `clear` the entry) and then offer consent-gated
> initialization.

This wording is the actual anti-nag control: the signal is advisory, and `disabled` is
authoritative.

---

## 10. Out of scope (deferred)

- Repo-committed / team-shared opt-out.
- Dashboard and HTTP API parity for policy read/write.
- Pattern expiry or "re-offer after N days". Entries now carry dates (§4.4), so this is
  feasible as a future behavior; only the behavior itself is deferred.
- Policy caching with file-mtime invalidation (per-call read is fine at this scale).
- Per-subdirectory policy within an adopted project.
- A dedicated decision-history / audit log (current dates only, §4.4).
- Automatic migration of memories written to global while a workspace was disabled back
  into project scope on re-enable (§6.4 caveat); users move them explicitly.

---

## 11. Tests

Pure (`Eling.Core.Tests`):

- `Resolve`: exact key wins; longest glob wins over shorter; `default` fallback; separator
  and case normalization; empty policy → `ask`; dates never affect resolution.
- Glob helper promotion does not change existing recall tag-matching behavior.

Store (`Eling.Backend.Tests`, injected clock):

- `Load` missing file → defaults; corrupt file → defaults + warning, file untouched.
- `Save` merge preserves `version` and unrelated keys; idempotent re-save; atomic write.
- **Document dates:** first effective write sets `createdAt == updatedAt`; later writes
  preserve `createdAt`; idempotent no-op writes do **not** bump `updatedAt`; every real
  change does; a hand-edited file without dates loads as null and is backfilled.
- **Entry timestamps:** creating an entry sets `createdAt == updatedAt`; re-saving the same
  decision changes nothing (no entry bump, no document bump); **changing** the decision
  value moves only the entry's `updatedAt` (its `createdAt` survives); `clear` removes the
  entry with its dates.
- `Set*` upserts and normalizes paths; `Clear*` removes the entry and falls back to the
  next level; `Clear*` on a missing entry is a no-op or `not_found` (tool maps it).

Tool (`Eling.Backend.Tests`):

- `memory_project_scope_policy` sets project / pattern / default; rejects unknown decision;
  rejects user-home; `target=pattern` requires a glob.
- Re-enable: `disabled` → `ask` flips `adoptable` back to true; `clear` removes the entry and
  falls back to the next level; an exact `ask` entry overrides a disabled pattern.
- Success payload echoes the policy including dates; `updatedAt` advances only on real
  change.

Behavior:

- `ProjectScopePosture`: `disabled` → `adoptable=false`, `policy=disabled`; `ask` unchanged.
- `memory_recall`: `projectScope` serialization (property names, both decisions).
- `memory_project_status`: agrees with recall and exposes the applicable entry's dates.
- `memory_save` on a disabled project → writes global, flags `projectScopeDisabled`, never
  `init-required`; on an `ask` project → still `init-required`.
- Round-trip: disable → re-enable (`ask`) restores `init-required`; the timeline
  (disabled at X, re-enabled at Y) is reconstructable from the entry's dates; memories
  already written to global while disabled are unaffected by re-enable.
- Existing backend suite (including `MemoryProjectToolsTests`) stays green.

---

## 12. Design decisions (resolved)

| # | Question | Decision | Rationale |
|---|---|---|---|
| 1 | Read semantics under `disabled` | **Global-only** for default/merged reads; explicit reference reads still work. | One coherent meaning: "this workspace is a global-scope citizen". Avoids the half-off variant where recall reads memories that saves refuse to write. |
| 2 | Project writes under `disabled` | **Route to global**, with `scope: "global"` + `projectScopeDisabled: true` + `note`. | A hard error invites retry loops and recreates the nagging this feature removes. Honesty is preserved by explicit response flags rather than by failing. |
| 3 | Specificity rule | **Longest glob wins**; ties by array order. | Length is a good proxy for specificity and stays stable as rules are appended; array-order precedence produces "why did my rule lose" surprises. No priority field (YAGNI). |
| 4 | Global `default: disabled` | **Allowed.** | Rejecting it means extra validation code against a value the schema already accepts; allowing it enables a coherent global-only-by-default mode as an explicit user choice. Absent file still defaults to `ask`. |
| 5 | Per-entry timestamps | **Yes — now**, as `createdAt` + `updatedAt` per `projects` / `patterns` entry. `createdAt` = first decision, immutable; `updatedAt` = last value change. `default` stays a plain string. | Nothing is shipped yet, so the schema change is free today and would be a costly migration later. Document-level dates alone cannot answer "when was *this* project disabled / re-enabled", which is the concrete audit question and a prerequisite for the deferred "re-offer after N days". |

---

## 13. Files added / changed (planned)

**Added:**

- `src/backend/Eling.Core/Scope/ProjectScopeDecision.cs`
- `src/backend/Eling.Core/Scope/ProjectScopeEntry.cs`
- `src/backend/Eling.Core/Scope/ProjectScopePattern.cs`
- `src/backend/Eling.Core/Scope/ProjectScopeResolution.cs`
- `src/backend/Eling.Core/Scope/ProjectScopePolicy.cs`
- `src/backend/Eling.Core/Matching/GlobPattern.cs` (promoted helper; the old `Eling.Core.MemoryRecall/GlobPattern.cs` was removed)
- `src/backend/Eling.Backend/Scope/IProjectScopePolicyStore.cs`
- `src/backend/Eling.Backend/Scope/JsonProjectScopePolicyStore.cs`
- `src/backend/Eling.Backend/Bootstrap/ProjectScopePosture.cs`
- `src/backend/Eling.Backend/Dtos/MemoryRecallProjectScopeDto.cs`
- `src/backend/Eling.Backend/Dtos/ProjectScopePolicyDto.cs`
- `src/backend/Eling.Backend/Dtos/MemoryProjectScopePolicyResultDto.cs`
- `src/backend/Eling.Backend/Mcp/Tools/MemoryProjectScopePolicyTool.cs`
- `tests/Eling.Core.Tests/ProjectScopePolicyTests.cs`
- `tests/Eling.Backend.Tests/JsonProjectScopePolicyStoreTests.cs`
- `tests/Eling.Backend.Tests/MemoryRecallToolProjectScopeTests.cs`
- `tests/Eling.Backend.Tests/MemoryProjectScopePolicyToolTests.cs`
- `tests/Eling.Backend.Tests/MemoryWriteToolProjectScopeTests.cs`

**Changed:**

- `src/backend/Eling.Backend/Bootstrap/ProjectScopePosture.cs` — policy-aware.
- `src/backend/Eling.Backend/Mcp/Tools/MemoryRecallTool.cs` — `projectScope` block.
- `src/backend/Eling.Backend/Mcp/Tools/MemoryProjectStatusTool.cs` — policy + dates.
- `src/backend/Eling.Backend/Mcp/Tools/MemoryWriteTool.cs` — disabled routing.
- `src/backend/Eling.Backend/Dtos/MemoryRecallProjectScopeDto.cs` — `policy` field.
- `src/backend/Eling.Backend/Mcp/McpServiceExtensions.cs` — register the store.
- `src/backend/Eling.Backend/Mcp/ServerInstructions.cs` — policy-aware wording.
- `docs/recall-architecture.md` — document the `projectScope` block.

---

## 14. Implementation notes

Landed on 2026-09-11. Both suites green: `Eling.Core.Tests` 119 passed, `Eling.Backend.Tests` 277 passed (per-csproj).

- **`adoptable` semantics.** Implemented as `!user-home && !own-scope && policy == ask` (a superset of "uninitialized") so it stays consistent with the pre-existing `memory_project_status` meaning and its tests; §6.1 was corrected to match.
- **Glob promotion.** The matcher moved from `internal Eling.Core.MemoryRecall.GlobPattern` to public `Eling.Core.Matching.GlobPattern`; `IntentionTriggerMatcher` imports it, and no behavior changed.
- **Glob normalization.** `~` is expanded and separators normalized to `/` when a pattern is written or loaded, so the on-disk glob is absolute (the `~/...` shown in this document is illustrative).
- **Clear signalling.** `ClearProjectAsync`/`ClearPatternAsync` throw `KeyNotFoundException` on a missing target; the tool maps it to `not_found`.
- **Disabled detection.** `MemoryWriteTool` awaits the store against the scoped service's `Cwd`; it reroutes only project-targeted saves (`scope` not `global`) and flags the response.
- **Recall stays lean.** The `projectScope` block carries no timestamps; dates are exposed by the policy tool and `memory_project_status`.
- **Fully async.** The store uses async file I/O behind a `Task`-returning interface, and the whole policy path (`ProjectScopePosture.EvaluateAsync`, `memory_project_status`, `memory_recall`, `memory_save`, `memory_project_scope_policy`) is `async/await`, consistent with the async storage engine. No `Task.FromResult` appears in this path.
