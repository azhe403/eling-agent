# Schema Read — Cache-First, Revalidate, and a Background Refresh

**Date:** 2026-11-07
**Status:** Draft (design review before implementation)
**Amends:** [2026-10-22-db-read-cache-design.md](2026-10-22-db-read-cache-design.md), [2026-11-06-db-metadata-cache-first-design.md](2026-11-06-db-metadata-cache-first-design.md)
**Scope:** `Eling.Core` (read service), `Eling.Dashboard` (databases page)

---

## 1. The expected flow

1. **Read.** If a snapshot exists it is returned at once, flagged as coming
   from the cache. No database round trip on the first paint.
2. **Revalidate.** The caller asks again for the real answer. If that ask
   cannot reach the database, it retries on an exponential ladder.
3. **Keep it fresh.** While the data is on screen it refreshes by itself
   every thirty seconds, so a table that has been open a while does not go
   stale.

Why this amends the earlier designs:

The 2026-10-22 design served the cache only when the database was
unreachable, so the first paint waited for a round trip. The 2026-11-06
design added a freshness window and answered a snapshot older than thirty
seconds as `refreshing` while a background refresh replaced it. That made a
reachable database look like it was not: the person opening the tree after a
few idle minutes saw a spinner, not tables. The complaint, in the user's
words: *"db harusnya sudah tersedia, tapi seolah blm."*

A reachable database must never look like it is not. A snapshot that answers
instantly is a better default than a correct-but-late answer, as long as the
snapshot is honest about being a snapshot and the real answer follows.

---

## 2. The three sources

| `source` | `fromCache` | When | What the tree shows |
|---|---|---|---|
| `cached` | true | A snapshot existed and the database was not read yet | The tables at once, no spinner; a revalidate is asked for |
| `live` | false | The database answered | The tables, no marker |
| `offline` | true | The revalidate could not reach the database | The tables, an `offline` marker, and a retry countdown |

`refreshing` is gone. It conflated "is this the latest answer" with "is the
database reachable", and answering the first with a spinner made a reachable
database look like an outage.

---

## 3. The read (backend)

```
GetSchemaAsync(connection, database, preferCache, ct)

  preferCache && cache hit
      -> return snapshot as `cached` (fromCache: true)
         No database read. No round trip.

  else
      -> live read
         success     -> cache it, return as `live`
         unreachable -> snapshot hit -> return as `offline`
                        snapshot miss -> throw
```

The freshness window stops being a read-path concept. It stays what the cache
uses to decide when an unchanged entry may keep its timestamp on a write —
that rule is unchanged, because a frozen timestamp makes the "cached · N ago"
marker lie.

The seven-day expiry is unchanged: an entry older than that is not served and
the read fails as it does today.

---

## 4. Revalidate, retry, and keep-fresh (dashboard)

The tree asks for a connection's schema in three cases, each handled by the
same `ensureSchema`:

1. **The connection is expanded, and a snapshot exists.** The snapshot
   paints at once (`cached`), and a revalidate is asked for a beat later —
   the bypassed read (`?refresh=true`) that reaches the database. If it
   answers, the tables are replaced by live data. If it cannot reach the
   database, the ladder schedules the next read.
2. **The revalidate cannot reach the database.** Exponential retry: two
   seconds doubling to a sixty-second ceiling, the attempt count cycling from
   one to ten. A database that stays dead for days must not go silent after a
   few minutes.
3. **The tables are already on screen.** A timer refreshes them every thirty
   seconds, so a table that has been open a while does not go stale. The
   refresh uses the cached read, so it never blocks the screen and never
   stacks (a slow database cannot start a second timer while one is running).

A manual refresh skips the cache and asks directly: the person asked for the
database, not for what we last knew.

---

## 5. MCP

`db_get_schema` stays live-first (`preferCache: false`) and never returns
`cached`. An agent that writes SQL from a stale schema writes it wrong, so it
only ever gets a snapshot on an outage, and that one is labelled `offline`.

---

## 6. Surfaces

**`Eling.Core`**
- `CacheSources`: `Cached` added, `Refreshing` removed.
- `DatabaseReadService`: cache hit → `cached`; freshness window no longer
  used by the read path; no background refresh, no cooldown.

**`Eling.Dashboard`**
- `useExponentialRetry` reduced to a single-source ladder (offline only).
- `ensureSchema` / `ensureDbSchema`: cache answer → schedule revalidate;
  live → stop; unreachable → ladder.
- A ten-second refresh timer while the tables are on screen.
- The badge shows `cached · N ago` / `offline` / nothing for live.

---

## 7. Tests

- Cache hit returns `cached`, `fromCache: true`, database never touched.
- Cache miss + healthy database → `live`.
- Cache miss + unreachable database → throws.
- `preferCache: false` → database read; unreachable → throws, snapshot not
  substituted.
- Unreachable database with a snapshot → `offline`, `fromCache: true`.
- A `cached` answer schedules exactly one revalidate, not several.

---

## 8. Not in this change

- Caching query results differently: they already fall back on an outage.
- A backend-side refresher: the caller asks again.
- TTL or size settings in the dashboard.
