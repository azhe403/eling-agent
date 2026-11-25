# Client Sorting Standard (Dashboard & Desktop)

2026-11-04 · Applies to every list panel endpoint. Adopted one endpoint at a time, never in a batch. **Paged grid views are not list panels** and may carry a documented exception (see §4); nothing else may.

## 1. Core rules

| Rule | Standard |
|---|---|
| Who sorts | **Server** sorts before `ToView`/map. Client never re-sorts. |
| Query param names | `sort` = column, `order` = direction (`asc` / `desc`). **Forbidden**: `dir`/`direction`. |
| Default | `sort=name&order=asc` → **Name A→Z**. |
| Unknown value | Falls back to default. **Not** 400. |
| Missing value (e.g. `lastConnectedAt` null) | Always the last row, in both directions. |
| Tie-break | Name A→Z, always. Order must never depend on registry/file order. |
| Direction | `asc` = natural order per column (name A→Z; `created`/`lastUsed` newest first). `desc` = mirror. |

Examples:

```http
GET /api/databases?sort=name&order=asc
GET /api/databases?sort=lastUsed&order=desc
GET /api/databases                     # = default
```

## 2. Client persistence

Priority is always: **URL → storage → default**.

**Web (dashboard)** - one lookup order:
1. `?sort=` / `?order=` in the URL wins.
2. Absent → stored pair in localStorage (`db.explorer.state`, or the page's blob).
3. Absent → `name` / `asc`.

Write to the URL only when it differs from the default, so shared links stay clean. Read on restore, not in a mount effect (React 19 lesson: use `useState` lazy initializer, see workspaces `sorting.ts`).

**Tab-scoped browse state (paged grid, web only)** - the `/browse` carve-out persists a *different* shape of state, and the lookup order is the same but scoped to the active tab:
1. For the **active tab**, a `?browse=` URL envelope (uri-encoded JSON `{filters, sort, manualCondition}`) wins over localStorage. It is written on every committed change, so a shared link lands on the exact view. It is omitted when the payload is empty.
2. Everything else (background tabs, or the active tab with no `?browse=`) hydrates from localStorage (`db.explorer.state`, per tab: `browseFilters` / `browseSort` / `manualCondition` / `filterOpen`). `browseLimit` and `browsePage` are deliberately **never** persisted.
3. Absent both → default (no filters, no sort, panel closed).

Order of application when both are present: URL overrides the active tab's stored values; stored values of the other tabs are used as-is.

**Desktop (Avalonia)** - no URL:
`DesktopSettingsStore` (settings document) → default `name`/`asc`. Same shape as the persisted `WorkspaceGroupMode`.

## 3. UI control

Segmented control: `role="group"`, `aria-label`, `aria-pressed`, positioned **directly under the panel header and above the list** (2026-10-21 position convention) - never stacked at the bottom. Stays visible while the tree scrolls. One button per key + one direction arrow (`↑`/`↓`).

## 4. Adoption (incremental)

One endpoint = one commit. New endpoints must follow the standard from birth.

| Endpoint | Status |
|---|---|
| `GET /api/databases` (`name`, `provider`, `created`, `lastUsed`) | **Adopted** - `DatabaseConnectionSort` (core), `GET /api/databases?sort=&order=` (backend), connections panel (dashboard). Design: `docs/superpowers/specs/2026-11-04-database-connection-sorting-design.md` |
| `POST /api/databases/{id}/browse` (paged grid view: arbitrary columns, multi-column sort, filter payload) | **Exception** - paged grid, not a list panel. Server sorts; POST body with `order` (never `direction`); multi-column via the panel, single-column cycle via the grid header; per-tab state (filters/sort/manual condition/filter-open) persists in localStorage and the **active tab only** mirrors to `?browse=` in the URL (URL > storage > default, see §2); no `browseLimit`/`browsePage` persistence; unknown column/op/order value answers 400 naming the offender, not fallback. Design: `docs/superpowers/specs/2026-11-16-databases-filter-sort-design.md` § Relation to `docs/sorting-standard.md` |
| Dashboard workspaces `?sort=&dir=` (`natural`/`flipped`) | Not yet standard - migrate `dir` → `order=asc/desc` (keep old alias for one release) |

MCP tools (`db_list_connections` etc.) read the store directly - **unchanged**.
