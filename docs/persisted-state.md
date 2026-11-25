# Page-State Persistence Standard (Eling Dashboard)

Status: Adopted 2026-11-16. First application: `/dashboard/skills`.

## Rule

Any filter, scope, search, sort, or mode state on a dashboard page MUST
persist to **both** of:

1. **The URL query string** — the source of truth. Reload, back/forward and a
   shared link all land on the same state. The page reads initial state from
   the URL; changing the state rewrites the URL.
2. **localStorage** — so a bare revisit (`/dashboard/x`) resumes where the
   user left off. The URL remains authoritative the moment it carries any
   state; a saved position is only used to fill a completely bare address.

## Canonical implementation

Use `src/lib/persisted-state.ts` — do not re-implement per page.

- `usePersistedState({ page, param, defaultValue, allowed? })` →
  `[value, setValue]`; the param is **omitted from the URL when the value is
  the default** (addresses stay clean); `allowed` rejects invalid stored
  values (fall back to the default silently); localStorage key is
  `eling:<page>:<param>` and is cleared when set back to default. Mount
  order: URL param > saved position (only on a bare address) > default;
  the localStorage read happens at the first render (lazy `useState`
  initializer), never in an effect and never via a redirect.
- `useResumePageParams(page, params)` → on mount, fills the URL **only when
  the query string is completely empty**. One line per page, after the
  `usePersistedState` hooks. Kept as a URL-normalization safety net;
  mount-time state restoration must not depend on it.

## Conventions

- Absent param == default value. Example: `origin=all` → no param; `trash`
  is `"0"` → absent; `tag=` → absent.
- Writes are `router.replace` with `scroll: false`. State is not history.
- URL param names are short and stable (`q`, `origin`, `sort`, `trash`,
  `tag`); never invent a page-local variant.
- A page that filters on the backend still drives the backend request from
  the same persisted state — nothing else may compute a second "current
  filter".
- `useSearchParams` requires a `<Suspense>` boundary around the client
  component (all skill/memory pages already do; new pages must too).

## What must be persisted (this page: `/dashboard/skills`)

| State | Param | Default |
|---|---|---|
| Search query | `q` | `""` |
| Origin filter | `origin` | `all` |
| Sort | `sort` (`name` \| `updated`) | `name` |
| Trash view | `trash` (`0` \| `1`) | `0` |
| Tag filter | `tag` | `""` |
| Detail panel selection | `skill` (skill name) | `""` |

Any new per-page state joins this table or gets reviewed against it.

## Required checks

- `pnpm --filter eling-dashboard exec tsc --noEmit`
- `pnpm --filter eling-dashboard test`
- Mock `next/navigation` in component tests (`useSearchParams`/`usePathname`/
  `useRouter` with a deterministic `URLSearchParams`), per the
  `use-codebase-search.test.ts` pattern.
- Bare visit must restore the saved position (`/dashboard/skills` with an
  empty query string resumes `q`/`origin`/`sort`/`trash`/`tag`).
- Invalid or stale storage values must degrade to the default, never crash.

## Anti-patterns

- Reading/writing `localStorage` inside ad-hoc effects (the old
  `memories:lastSelection` style) — use the hook.
- Persisting into component state and syncing outwards with a one-shot effect.
- Page-local URL writers that race with the hook's `router.replace`.
- Persisting unbounded values (multi-KB lists) — persist the selection key,
  not the payload.
