# Client Sorting Standard (Dashboard & Desktop)

2026-11-04 · Applies to every list endpoint. Adopted one endpoint at a time, never in a batch.

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

**Web (dashboard)** — one lookup order:
1. `?sort=` / `?order=` in the URL wins.
2. Absent → stored pair in localStorage (`db.explorer.state`, or the page's blob).
3. Absent → `name` / `asc`.

Write to the URL only when it differs from the default, so shared links stay clean. Read on restore, not in a mount effect (React 19 lesson: use `useState` lazy initializer, see workspaces `sorting.ts`).

**Desktop (Avalonia)** — no URL:
`DesktopSettingsStore` (settings document) → default `name`/`asc`. Same shape as the persisted `WorkspaceGroupMode`.

## 3. UI control

Segmented control: `role="group"`, `aria-label`, `aria-pressed`, positioned **directly under the panel header and above the list** (2026-10-21 position convention) — never stacked at the bottom. Stays visible while the tree scrolls. One button per key + one direction arrow (`↑`/`↓`).

## 4. Adoption (incremental)

One endpoint = one commit. New endpoints must follow the standard from birth.

| Endpoint | Status |
|---|---|
| `GET /api/databases` (`name`, `provider`, `created`, `lastUsed`) | Design only — `docs/superpowers/specs/2026-11-04-database-connection-sorting-design.md`, not yet implemented |
| Dashboard workspaces `?sort=&dir=` (`natural`/`flipped`) | Not yet standard — migrate `dir` → `order=asc/desc` (keep old alias for one release) |

MCP tools (`db_list_connections` etc.) read the store directly — **unchanged**.
