# Database Metadata Cache-First — Design Amendment

**Date:** 2026-11-06
**Status:** Draft (awaiting approval)
**Amends:** [2026-10-22-db-read-cache-design.md](2026-10-22-db-read-cache-design.md)
**Scope:** `Eling.Core` (read service, schema record), `Eling.Backend` (schema endpoint, MCP tool output), `Eling.Dashboard` (databases page), tests

---

## 1. What changes, and why

The 2026-10-22 spec made the cache a **fallback**: a metadata read goes to the
database, and only when the adapter throws `DatabaseUnavailableException` does
the cache answer. Every first paint therefore waits for one round trip per
connection — slow on a good day, painful on a bad one, and the tree crawls
while a dead server times out.

This change makes the two **metadata** reads cache-first on the dashboard
surface:

- a connection's schema (`GET /api/databases/{id}/schema`)
- a server-level connection's database list (same endpoint, same read)

If a cached snapshot exists it is answered at once and refreshed behind the
response. When the database cannot be reached and only the cache can answer,
the response says `offline` and carries the snapshot's remaining lifetime.

**Query results are not cache-first.** Serving yesterday's rows as the answer to
a query the user just wrote would be a lie the user cannot see through. Queries
keep today's behaviour: live, cache only on failure.

The original spec's "no background refresher" non-goal is superseded here for
metadata reads only.

---

## 2. State model

`DatabaseSchemaInfo` gains three properties:

| Property | Type | Meaning |
|---|---|---|
| `Source` | `string` | `"live"`, `"refreshing"`, or `"offline"` |
| `CacheExpiresAt` | `DateTimeOffset?` | when the snapshot stops being served (`cachedAt + 7 d`) |
| `CacheTtlSeconds` | `long?` | remaining lifetime, floored at 0 |

`FromCache` stays, because the dashboard's retry loops and the audit event read
it. It is `Source != "live"`.

`Source` is a string, not an enum: the API has no string-enum converter, so an
enum would serialize as `1`.

| Situation | `Source` | `FromCache` | Marker in the UI |
|---|---|---|---|
| read went to the database | `live` | false | none |
| cache younger than the freshness window | `live` | false | none |
| cache served, refresh fired | `refreshing` | true | `refreshing… · 4 min ago` |
| cache served, last refresh failed | `offline` | true | `offline · 4 min ago · expires in 6 d` |

A cache entry inside the freshness window is reported as `live`: a successful
refresh within that window is the evidence that the snapshot is current. This
also keeps `fromCache === false` meaning what the dashboard already assumes, so
its "stop retrying" guard keeps working untouched.

---

## 3. Read flow

```
GetSchemaAsync(connection, database, preferCache, refresh, ct)

  if (!preferCache || refresh)      -> live read, cache it, return live
  if no cached entry                -> live read (fails as today when down)

  age = now - cachedAt
  if (age < FreshWindow)            -> return entry as live

  if (last refresh failed && now - lastAttempt < RetryBackoff)
                                    -> return entry as offline
  fire background refresh (single-flight)
                                    -> return entry as refreshing
```

The background refresh:

- runs on `Task.Run` with `CancellationToken.None`, so it outlives the request;
  `DatabaseReadService` is a singleton holding only the file-backed cache and a
  logger, so nothing scoped is captured;
- is single-flight per cache key (`ConcurrentDictionary<string, byte>` with
  `TryAdd`/`TryRemove`);
- swallows `DatabaseUnavailableException` and records the failure in an
  in-memory `ConcurrentDictionary<string, DateTimeOffset>`; any other exception
  is logged and dropped — a background failure must never surface to a caller;
- on success writes the cache and clears the failure record.

Constants: `FreshWindow = 30 s`, `RetryBackoff = 30 s`.

The failure record is in memory only. After a restart the first cache hit probes
the database once, which is the desired behaviour.

---

## 4. Refresh bypass

An explicit refresh must reach the database, not the cache. The endpoint gains
`?refresh=true`, which skips the cache-first branch. The dashboard sends it from
"Refresh schema" and from `ensureSchema(id, { force: true })`.

Without this, "Refresh" would return the same cached snapshot and look broken.

---

## 5. Surfaces

**HTTP** — `GET /{id}/schema` passes `preferCache: true`. The record is
serialized as-is, so `source`, `cacheExpiresAt`, `cacheTtlSeconds` reach the
dashboard with no DTO change.

**MCP** — `db_get_schema` keeps `preferCache: false`: the agent acts on what it
reads, and a silent stale schema makes it write wrong SQL. It still gets the
cached fallback on an outage, now labelled `offline` with the TTL.

**Dashboard** — `SchemaInfo` gains the three fields; `fetchSchema` gains a
`refresh` flag; `loadSchema` and the `force` path pass it. The badge switches on
`source` instead of `fromCache`. The existing retry loops (`ensureSchema`,
`ensureDbSchema`) are unchanged: they retry while `fromCache` is true, so a
`refreshing` answer converges to `live` within one poll.

**Audit** — `FromCache` keeps its current meaning; no audit schema change.

---

## 6. What does not change

- The cache file, its keys, and the 7-day ceiling. Expiry is computed as
  `cachedAt + IDatabaseReadCache.DefaultMaxAge`, so the store needs no new field.
- The Redis read path, which has its own cache and its own 24 h ceiling.
- Query caching, invalidation on mutation, and connection edit/delete.
- The connection form's "list databases" call, which still runs live against
  unregistered credentials.

---

## 7. Tests

New `DatabaseReadServiceCacheFirstTests`:

1. Cached entry younger than `FreshWindow` is returned as `live` and the adapter
   is never called.
2. Cached entry older than `FreshWindow` is returned as `refreshing`, and the
   adapter is called exactly once even when two reads race.
3. A failing background refresh flips the next read to `offline` with
   `CacheTtlSeconds > 0` and does not probe again inside `RetryBackoff`.
4. A successful refresh makes the next read `live`.
5. `refresh: true` skips the cache and returns live data.
6. No cached entry + unreachable database still throws (unchanged).
7. `preferCache: false` (MCP path) goes live first.

Existing `JsonDatabaseReadCacheTests` and the endpoint tests must keep passing;
the endpoint tests that assert `fromCache` behaviour are updated for `source`.

---

## 8. Not in this change

- Cache-first for query results.
- Pushing refreshed data to the browser (SSE or websockets). The dashboard's
  existing retry poll is the delivery mechanism.
- TTL reporting for the Redis cache.
- A configurable freshness window.
