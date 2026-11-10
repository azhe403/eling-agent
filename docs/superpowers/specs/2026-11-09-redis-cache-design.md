# Read Cache Schema — Central Reference

**Date:** 2026-11-09
**Status:** Static reference (amends nothing, summarizes implemented code)
**Scope:** `Eling.Core.Database` (DB + Redis read caches), `Eling.Backend` (HTTP + MCP), `Eling.Dashboard` (databases page)

This is the single place that names the cache files, the record shapes, and the
read flow. The narrative specs (`2026-10-22-db-read-cache-design.md`,
`2026-11-06-db-metadata-cache-first-design.md`, `2026-11-07-schema-cache-first-revalidate-design.md`)
and the transport designs (`2026-10-26-db-connection-redis-design.md`,
`2026-10-27-redis-db-tree-design.md`) still own the why. This doc owns the
what, so the next reader does not have to reconstruct it from code.

---

## 1. Files

Both hosts (dashboard HTTP host and MCP stdio host) share one directory under
`CentralAgentDirectory.Resolve()`.

| File | About | Serializer |
|---|---|---|
| `database-cache.json` | SQL reads: schema + query results | `JsonDefaults.Shared` (camelCase, indented, case-insensitive) |
| `database-redis-cache.json` | Redis reads: database list + scan pages + single-key inspections | same |

Writes are temp file + `File.Move(temp, target, overwrite: true)` under a
named mutex (`CacheFileLock.NameFor(dir, "db")` / `"redis"`). A half-written
file never replaces the good one. An unreadable or missing file is an empty
cache, logged as a warning, never an exception. Entries are pruned on read
and on write.

Nothing in either file is a credential. Cache keys are built from the
connection **id** (ULID), never the connection string. Query SQL text and
Redis key names are hashed (SHA256 / `Sha256Hash`) instead of stored verbatim,
so the file is not a log of what was asked.

---

## 2. DB cache shape (`IDatabaseReadCache` / `JsonDatabaseReadCache`)

**File record:** `DatabaseReadCacheFile`
- `Schemas: List<CachedSchemaEntry>`
- `Queries: List<CachedQueryEntry>`
- `DatabaseLists: List<CachedDatabaseListEntry>` (server-level list, shares the schema entry)

**Entry records:**

| Entry | Key fields | Value fields |
|---|---|---|
| `CachedSchemaEntry` | `ConnectionId, Database` | `Schema (DatabaseSchemaInfo), CachedAt` |
| `CachedQueryEntry` | `ConnectionId, Database, MaxRows, SqlHash (SHA256 of SQL)` | `Result (QueryResult; cells normalized via JsonCell), CachedAt` |
| `CachedDatabaseListEntry` | `ConnectionId` | `Databases (IReadOnlyList<string>), CachedAt` |

**Ceilings:** `DefaultMaxAge = 7 days`. Stale after 24 h (still served when
appropriate, marked). Expired past 7 d is not served and is pruned. Query
entries capped per connection (most recent 200, `MaxQueryEntriesPerConnection`).
Successful `Set` whose value is unchanged still refreshes `CachedAt` if and
only if the freshness window rule allows it (schema keeps timestamp when unchanged
inside the window — frozen timestamp must not make the marker lie), otherwise the
write is skipped.

---

## 3. Redis cache shape (`IRedisReadCache` / `JsonRedisReadCache`)

**File record:** `RedisCacheFile` (private sealed record in `JsonRedisReadCache`)
- `Databases: List<CachedRedisDatabasesEntry>`
- `Keys: List<CachedRedisKeyEntry>`
- `Pages: List<CachedRedisPageEntry>`

`Empty()` is `new([], [], [])`. Older cache files written before `Pages` existed
lack that property; the gate's `Load(Empty)` then deserializes `Pages` as null,
and `Normalize` coerces it to `[]` on every read/write, so migration is silent.

**Entry records:**

| Entry | Key fields | Value fields |
|---|---|---|
| `CachedRedisDatabasesEntry` | `ConnectionId` | `Databases: IReadOnlyList<RedisDatabaseInfo>, CachedAt` |
| `CachedRedisKeyEntry` | `ConnectionId, Database (int), KeyHash (SHA256 hex)` | `Result: KeyInspectResult, CachedAt` |
| `CachedRedisPageEntry` | `ConnectionId, Database (int), Prefix (Ordinal), Cursor (long), PageOffset (int), PageSize (int)` | `Page: KeyPage, CachedAt` |

`CachedRedisPageEntry` is exactly one lazy-tree page: `Prefix, Cursor,
PageOffset, PageSize` is the page identity. Page content is the full
`KeyPage` record (`Prefix, Nodes, Keys, Cursor, PageOffset, Complete,
Scanned, FromCache=false, CachedAt=null`) as the server returned it live.
On a cache-first hit the service re-stamps it with `FromCache=true,
CachedAt=<entry CachedAt>` without otherwise mutating it — leaf TTLs travel
with the snapshot and age under the marker, the honest equivalent of the
schema `cached · N ago` chip.

**Ceilings:** `IRedisReadCache.MaxAge = 24 h`. Expired entries
(`UtcNow - CachedAt > MaxAge`) are not served and are not returned as hits.
`MaxKeysPerConnection = 500` (ordinal-by-CachedAt eviction per connection).
`MaxPagesPerConnection = 1000` (same eviction shape: drop oldest `CachedAt`
for that connection).

**Key storage note:** the inspected key itself is hashed for lookup
(`HashKey`), but kept inside `KeyInspectResult`, so the cached answer still
names the key. Page identity is stored plaintext because `Prefix` is bounded
to 1024 chars and the cursor is opaque — hashing it would only obscure debugging.

---

## 4. Records

Both cache families expose the same two last fields on their value records:

```csharp
public bool FromCache { get; init; }          // false on a live answer
public DateTimeOffset? CachedAt { get; init; } // the snapshot moment, null when live
```

- `QueryResult.FromCache`, `DatabaseSchemaInfo.FromCache`
- `RedisDatabaseInfo(..., bool FromCache = false, DateTimeOffset? CachedAt = null)`
- `KeyPage(..., bool FromCache = false, DateTimeOffset? CachedAt = null)`
- `KeyInspectResult(..., bool FromCache = false, DateTimeOffset? CachedAt = null)`

Init-only, so live adapters and `ScanLiveAsync` construct values without
knowing about the cache. The read service stamps them with `with`.

For DB schema, `DatabaseSchemaInfo` additionally carries `Source` (`"live" |
"cached" | "offline"`), `CacheExpiresAt` (`cachedAt + 7 d`), and
`CacheTtlSeconds`. Redis records carry only `FromCache/CachedAt`; the
dashboard shows the age via `dbRowAsOf` / `pageAsOf` (`"cached as of <time>"`).
A cached Redis value is therefore indistinguishable between "painted from
snapshot, live pending" and "live failed, offline snapshot" — the revalidate
loop collapses both into one `fromCache: true` marker.

`KeyLeaf.TtlSeconds` is a snapshot of seconds-left at write time and is served
verbatim with the page. `KeyInspectResult.TtlSeconds` is the one place the
cache recomputes: `RemainingTtl` derives seconds-left from the stored absolute
`ExpiresAt` so a cached inspection never overstates a key's life.

---

## 5. Read flow (get-or-set)

**Metadata reads (`GetSchemaAsync`, `Redis ListDatabases`, `Redis Scan` pages) — cache-first:**

```
preferCache && TryGet == hit
    -> return snapshot as cached (FromCache: true, CachedAt)
       No round trip.
else
    -> live read
         success     -> Set + return live
         unreachable -> snapshot hit -> return cached (offline)
                        snapshot miss -> throw
```

`preferCache` defaults to `true` on the HTTP surface, `false` on MCP
(`db_get_schema` / `redis_scan` with `preferCache: false`), and `?refresh=true`
on the HTTP surface means `preferCache: false` (bypass). A caller that already
has a live answer never asks again (`ensureSchema` short-circuits on
`source === "live"`).

**Query/write reads (`QueryAsync`, `InspectAsync`, console `SCAN`, `ExecuteCommand`) — live-first:**

```
live read
  success             -> Set + return live
  KeyNotFound         -> RemoveKey (inspect) then throw
  unreachable         -> snapshot hit -> return cached (offline)
                         snapshot miss -> throw
```

There is no `preferCache` early return for queries. `@` an agent or a console
that asks a question must never be served a value the server did not produce
for that question.

**Failure split (both families):** "cannot reach the server" is the only
trigger. Wrong password (`NOAUTH/WRONGPASS`), command rejection (`WRONGTYPE`),
invalid syntax, and missing keys are real answers and are never papered over
with a snapshot. `RedisKeyReadService.IsUnreachable = (RedisConnectionException
or TimeoutException) && !IsCredentialFailure`; the SQL family wraps open
failures in `DatabaseUnavailableException`.

---

## 6. Reachability and refresh seams

| Surface | `preferCache` | Cache-first paint | Revalidate | Manual refresh |
|---|---|---|---|---|
| Dashboard `GET /{id}/schema` | `refresh != true` (default `true`) | yes, `cached` | yes, `?refresh=true` one background call, then exponential ladder on offline | `?refresh=true` |
| Dashboard `GET /{id}/keys` | `refresh != true` (default `true`) | yes, snapshot paints | yes, background `?refresh=true` page replace | Root/Browse sends `refresh=true` |
| Dashboard `GET /{id}/keys/databases` | `refresh != true` (default `true`) | yes | yes, background `?refresh=true` list replace | expand uses cached paint, revalidate does live |
| Dashboard `GET /{id}/keys/inspect` | — (fallback-only) | no | no | user re-click |
| MCP `db_get_schema` | `false` | no | no | tool has no refresh flag |
| MCP `redis_scan` | `false` | no | no | tool has no refresh flag |
| MCP `redis_inspect` | — (fallback-only) | no | no | — |
| REST `POST /{id}/keys/command` (incl. `SCAN`) | `false` for its internal scan | no | no | console typing is the refresh |

The dashboard's revalidate is a second `fetch(..., { refresh: true })` fired
immediately after the cached paint lands, replacing the snapshot when the
live answer arrives. Offline revalidates keep the cached paint and the ladder
does the waiting. A ten-second timer while a connection is expanded in the
tree keeps the snapshot fresh while it is on screen; the timer uses the
cached read so it never blocks the screen and never stacks.

---

## 7. Invalidation

**DB:**
- Successful read replaces that entry (with the freshness-window timestamp rule)
- Mutation (`POST /{id}/mutate`), connection edit (`PUT /{id}`), connection delete (`DELETE /{id}`) → `InvalidateConnection(id)` drops the whole connection

**Redis:**
- Successful scan/database-list read replaces that entry (`SetPage` / `SetDatabases`)
- `InvalidateConnection(id)` drops databases + keys + pages for that connection (file `Normalize`d before counting so a pre-`Pages` file is not lost)
- `RemoveKey(conn, db, key)` drops one inspected key (unknown command that mutated its operand, missing-key confirm)
- `ExecuteCommand` drops the operand key's inspection via `RemoveKey` when `args.Count > 0`

Connection edits and deletes already call `redisReadCache.InvalidateConnection(id)` beside `readCache.InvalidateConnection(id)` in `DatabaseEndpoints`.

---

## 8. Not in this doc

- The SQL file's `JsonCell` cell-normalization (scopes the cached cell to what JSON can hold)
- A backend-side refresher (spec §4 / §8: the caller asks again)
- Per-connection cache tuning in the dashboard
- `Inspect` cache-first or any auto-refresh toggle UI — inspected keys stay live-first fallback, `selectRedisKey` still asks the server each time
