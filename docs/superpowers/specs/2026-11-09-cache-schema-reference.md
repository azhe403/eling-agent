# Read Cache Schema — Static Reference

**Date:** 2026-11-09 (amended 2026-11-10)
**Status:** Reference (Implemented)
**Amends:** `2026-10-22-db-read-cache-design.md`, `2026-11-06-db-metadata-cache-first-design.md`, `2026-11-07-schema-cache-first-revalidate-design.md`, `2026-11-10-redis-expiry-and-cache-contract-design.md`
**Scope:** `Eling.Core.Database` read caches, `Eling.Backend` HTTP+MCP surface, `Eling.Dashboard` databases page

This doc is the static home for the cache schema: what file, what records,
what keys, and which read pattern each endpoint uses. The dated design specs
own the why. This doc owns the what, so the reader never has to read code to
learn the shape.

---

## 1. Files

Both hosts share one directory under `CentralAgentDirectory.Resolve()`. The
dashboard HTTP host and the MCP stdio host see the same snapshot.

| File | Writer | Format | Serializer |
|---|---|---|---|
| `database-cache.json` | `JsonDatabaseReadCache` | DB schema + query results | `JsonDefaults.Shared` (camelCase, indented, case-insensitive) |
| `database-redis-cache.json` | `JsonRedisReadCache` | Redis databases + scan pages + single-key inspections | same |

Write discipline: temp file + `File.Move(temp, target, overwrite: true)` under
a named mutex (`CacheFileLock`). A half-written file never replaces the good
one. A missing, blank, or unreadable file is an empty cache, never an
exception. Entries are pruned on read and on write (maxAge + caps).

Neither file contains a connection string, username, password, or host. Cache
keys are built from the connection **id** (ULID) only. Long atoms (SQL text,
Redis key name) are hashed (SHA256) instead of stored verbatim, so the file
is not a log of what was asked.

---

## 2. Record shapes

### 2.1 DB (`IDatabaseReadCache` → `JsonDatabaseReadCache`)

**File record:** `DatabaseReadCacheFile`
- `Schemas: List<CachedSchemaEntry>`
- `Queries: List<CachedQueryEntry>`
- `DatabaseLists: List<CachedDatabaseListEntry>` (server-level list shares the schema entry)

| Entry | Key fields | Value |
|---|---|---|
| `CachedSchemaEntry` | `ConnectionId, Database` | `Schema: DatabaseSchemaInfo, CachedAt` |
| `CachedQueryEntry` | `ConnectionId, Database, MaxRows, SqlHash (SHA256)` | `Result: QueryResult, CachedAt` |
| `CachedDatabaseListEntry` | `ConnectionId` | `Databases: IReadOnlyList<string>, CachedAt` |

`QueryResult` cells are stored via `JsonElement` and unwrapped on the way
out (`JsonCell.ToClrValue`) so a `long` stays a `long`, never a `JsonElement`.

**Records carry:** `bool FromCache { get; init; }`, `DateTimeOffset? CachedAt`,
plus `DatabaseSchemaInfo.Source` (`"live" | "cached" | "offline"`),
`CacheExpiresAt`, `CacheTtlSeconds`. Redis records carry only the first two
(see §3); a Redis `FromCache: true` therefore covers both `cached` and
`offline` without a third state.

### 2.2 Redis (`IRedisReadCache` → `JsonRedisReadCache`)

**File record:** `RedisCacheFile`
- `Databases: List<CachedRedisDatabasesEntry>`
- `Keys: List<CachedRedisKeyEntry>`
- `Pages: List<CachedRedisPageEntry>`

`Empty()` is `new([], [], [])`. Files written before `Pages` existed
deserialize with `Pages == null`; `Normalize(file)` coerces null lists to `[]`
on every read and write, so migration is silent.

| Entry | Key fields | Value |
|---|---|---|
| `CachedRedisDatabasesEntry` | `ConnectionId` | `Databases: IReadOnlyList<RedisDatabaseInfo>, CachedAt` |
| `CachedRedisKeyEntry` | `ConnectionId, Database (int), KeyHash (SHA256 hex)` | `Result: KeyInspectResult, CachedAt` |
| `CachedRedisPageEntry` | `ConnectionId, Database (int), Prefix (Ordinal), Cursor (long), PageOffset (int), PageSize (int)` | `Page: KeyPage, CachedAt` |

`CachedRedisPageEntry` is exactly one lazy-tree page. The page identity is
`(Prefix, Cursor, PageOffset, PageSize)` — two callers that ask for the same
slice of the same prefix share the entry; a different page size is a different
slice and gets its own entry. `KeyHash` hides the inspected key name in the file
while `KeyInspectResult.Key` still names it inside the value.

**Records carry:** `FromCache` + `CachedAt` on every value record:

- `RedisDatabaseInfo(Index, Keys, FromCache=false, CachedAt=null)`
- `KeyPage(Prefix, Nodes, Keys, Cursor, PageOffset, Complete, Scanned, FromCache=false, CachedAt=null)`
- `KeyInspectResult(Key, Type, TtlSeconds, ExpiresAt, Length, Encoding, Preview, FromCache=false, CachedAt=null)`

`KeyLeaf(Key, Type, TtlSeconds, ExpiresAt = null, Expired = false)` is the one
record that carries both the expiry *and* the carried-over flag; see §4.4.

The cached `KeyPage` stores the live answer after the merge of §4.4 (keys the
cache already held that left the keyspace are carried over `Expired`). Leaf
TTLs travel with the snapshot but are recomputed from the stored absolute
`ExpiresAt` on read (`PageWithCurrentTtl`) — see §2.2 note on inspections for
the same rule.

**TTL on a cached inspection:** the stored `TtlSeconds` is a snapshot of
seconds-left at write time and would overstate the key's life if served an hour
later. `RemainingTtl` recomputes it from the stored absolute `ExpiresAt`
(`expires - UtcNow`, floored at `0`; no expiry keeps `-1`). Leaf TTLs in a
cached page follow the same rule: the absolute `ExpiresAt` is stored, and
`ScanAsync` rewrites each leaf's `TtlSeconds` from it on cache hits and on
offline fallback, so the countdown the client draws is live rather than the
frozen number the scan returned. A page still keeps the `cached as of` marker —
the honest equivalent of the schema `cached · N ago` chip.

---

## 3. Ceilings

| Cache | `MaxAge` | Admission on read | Cap |
|---|---|---|---|
| DB | `DefaultMaxAge = 7 days` | `< 24 h` fresh, `24 h–7 d` stale (still served), `> 7 d` expired, not served, pruned | 200 query entries per connection |
| Redis | `MaxAge = 24 h` | `< 24 h` served, `> 24 h` expired, not served, pruned | `MaxKeysPerConnection = 500` (oldest `CachedAt` dropped per connection), `MaxPagesPerConnection = 1000` |

A `Set` whose payload is unchanged still refreshes `CachedAt` when the
freshness rule allows it — a frozen timestamp would make the marker lie.

---

## 4. Read flows

### 4.1 Metadata (DB `GetSchema`, Redis `ListDatabases` + `Scan`)

These are the reads a tree paints. They are **cache-first**.

```
preferCache && TryGet == hit
    -> return snapshot as cached (FromCache: true, CachedAt)
       No round trip.
else
    -> live read
         success     -> Set + return live (FromCache: false)
         unreachable -> snapshot hit -> return cached (offline, FromCache: true)
                        snapshot miss -> throw
```

- HTTP dashboard: `preferCache = ?refresh != true` (default `true`). No
  `?refresh` means "paint the snapshot at once", `?refresh=true` means "ask the
  server, not what we last knew".
- MCP: `db_get_schema` and `redis_scan` call with `preferCache: false` — live-first.
  Only ever gets a snapshot on an outage, then labelled `FromCache: true`.
- Console `SCAN` via `ExecuteCommand` is also a query-like read and calls
  `ScanAsync(..., preferCache: false)` so the console never serves a snapshot
  as if the user had just scanned.

### 4.2 Query-like (DB `QueryAsync`, Redis `InspectAsync`)

These are the reads the user just wrote (SQL rows, one key's value and preview).
They are **live-first, fallback-only**, never cache-first:

```
live read
  success     -> Set + return live
  KeyNotFound -> RemoveKey (inspect only) then throw
  unreachable -> snapshot hit -> return cached (FromCache: true)
                 snapshot miss -> throw
```

Serving yesterday's rows as the answer to a query the user just typed would be
a lie the user cannot see through. The `@` symbol in the Redis docs is the same
debt: single-key inspect answers with a live `TYPE/TTL/length/preview` and only
falls back when the server cannot be reached.

### 4.3 The carried-over key (`KeyLeaf.Expired`)

An agreed dashboard contract: a key whose TTL reaches zero does not disappear
from the tree on its own. `JsonRedisReadCache.SetPage` does the merge, not the
client:

```
previous = entry(ConnectionId, Database, Prefix, Cursor, PageOffset, PageSize).Page
fresh    = incoming page

retained = previous.Keys where key not in fresh.Keys
store    = fresh with Keys = fresh.Keys ++ retained.Map(_ with {
                       Expired   = true,
                       TtlSeconds = -1,
                       ExpiresAt  = null })
```

Properties:

- Nothing is invented. Only keys the cache already had are re-added — a key the
  scan never saw cannot reappear.
- A key the server still reports wins outright: `Expired` is gone, whatever the
  previous page thought. `Pages_DropTheFlagWhenTheKeyComesBack` pins this.
- `Keys` order is `fresh ++ retained`: the carried rows land at the end of the
  page, so the tree does not reshuffle under the reader.
- The flag lives in the file, so it survives process restarts and is honoured
  on every read path (cache-first, offline fallback, manual refresh).

The client renders the flag (`redis-tree.tsx`, label `expired`, row struck
through and dimmed). It does not decide whether a key is gone — the backend's
page cache does.

### 4.4 The expiry recheck (`GET/POST /{id}/keys/probe`)

The dashboard's countdowns are wall-clock claims against `expiresAt`. A claim
that reaches zero is not proof of absence: a writer may have rewritten the key
since the scan. So once a key comes due the dashboard asks, and if the answer
is "gone" it refetches for that page immediately rather than waiting for §7.

- Coalesced, not debounced: short-lived keys never go quiet, so a debounce
  would starve. The buffer collects for `PROBE_COALESCE_MS = 250` with a hard
  `PROBE_MAX_WAIT_MS = 1000`, then one request carries the bunch
  (`POST` with `keys[]`, capped at 500).
- A `gone` answer refetches that page ahead of the AutoRefresh tick (which may
  be minutes away), because the keyspace demonstrably moved.
- `gone` triggers one refetch per page per flush, with a 1s floor
  (`GONE_REFETCH_MIN_INTERVAL_MS`) so a steady stream of expiring keys does not
  turn into a refetch loop.

The client renders the recheck output exactly like §4.3.

---

## 5. Reachability split

"Cannot reach the server" is the only trigger. The two families name it
differently but implement the same check:

- Redis: `IsUnreachable = (RedisConnectionException or TimeoutException) && !IsCredentialFailure(NO AUTH/WRONGPASS)`
- SQL: an adapter failure opening the connection is wrapped as
  `DatabaseUnavailableException`; an execution failure propagates unchanged.

A command rejection (`WRONGTYPE`, unknown command), a syntax error, or a
refused password is a real answer and is never papered over with a snapshot.

---

## 6. Invalidation

| Event | Effect |
|---|---|
| Successful read | That entry is replaced with the fresh answer (`Set*`) |
| `Inspect` key vanished (`KeyNotFound`) | `RemoveKey(id, db, key)` — the cached answer is now a lie |
| Redis `ExecuteCommand` with an operand | `RemoveKey(id, db, args[0])` — the operand's type/TTL/value may have changed |
| DB mutation / connection edit / connection delete | `InvalidateConnection(id)` for both caches |
| Redis connection edit / delete | `InvalidateConnection(id)` drops databases + keys + pages |
| Entry older than `MaxAge` | pruned on read/write, never served |

---

## 7. Dashboard revalidate + keep-fresh

Per `2026-11-07 §4` applied to the Redis tree:

1. **Cache paint.** `loadRedisDatabases` / `fetchRedisPrefix` fetch without
   `?refresh`. The backend answers a snapshot at once (`fromCache: true`);
   the counts line shows `cached as of <time>` via `dbRowAsOf` / `pageAsOf`.
2. **Revalidate.** If the answer was cached, the hook immediately fires a
   second `fetch(..., { refresh: true })` behind it and replaces the snapshot
   with live data. Manual Root/Browse goes straight to `refresh: true` (bypass)
   because the reader asked for the server, not what was last known.
3. **Offline ladder.** If the revalidate cannot reach the server the cached
   paint stays and the next tick asks again. Revalidates never show as errors
   while a cached paint is already on screen — no toast spam for one dead host.
4. **Keep-fresh.** An effect on the active Redis tab fires `fetchRedisPrefix(
   ..., refresh: true)` every `KEEP_FRESH_MS = 30000` for the root prefix while
   the tab is on screen. It uses the in-flight counter (`redisSetLoading`) so
   a slow server cannot stack two of them and the screen never blocks.
5. **Expiry recheck.** A per-page probe effect fires `fetch(.../keys/probe)`
   once keys come due (§4.4). If a key is truly gone it refetches its page
   ahead of the tick, and the carried-over row stays flagged expired until the server reports the key again.
6. **AutoRefresh interval.** The dashboard interval is user-set (`10/15/30/60s`,
   default 30s), persisted in localStorage. It is the backstop, not the
   recovery path: the expiry recheck is the one that notices first.

`Load more` continuations are cache-first by page key
(`conn:db:prefix:cursor:pageOffset`), merge via dedup, and keep the original
`FromCache/CachedAt` union so a cached root never claims to be live.

---

## 8. What stays uncached

- `test-connection`, `/{id}/test`, `/detect`, `/import`, `/list-databases` for
  unregistered credentials — reads a server that is not the registered one, so
  caching it under the connection key would poison later reads.
- Query results cache-first — the fallback of §4.2 is the whole story.
- TTL/size/off-toggle settings in the dashboard.
