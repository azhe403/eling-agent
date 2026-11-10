# Redis Key Tree — Expiry & Cache Contract

**Date:** 2026-11-10
**Status:** Accepted (Implemented)
**Amends:** `2026-10-26-redis-db-tree-design.md`, `2026-11-07-schema-cache-first-revalidate-design.md`, `2026-11-09-cache-schema-reference.md`
**Scope:** `Eling.Core.Database` read caches, `Eling.Backend` HTTP surface, `Eling.Dashboard` databases page → Redis tab

This is the patokan for the Redis key tree: who is responsible for expiry, who
is responsible for cache, and what the tree may never do. The dated design specs
own the why. This one owns the what, and pins the behaviour that exists in code
today.

---

## 1. The two rules

**Rule 1 — An expired key never disappears.**
A key whose TTL reaches zero stays in the tree, struck through and dimmed,
labelled `expired`. A row that vanishes on its own is indistinguishable from a
bug, and the reader loses the thread of what they were watching.

**Rule 2 — The cache is server-side; the client only renders.**
`JsonRedisReadCache` decides whether a row is expired. The dashboard renders
the flag. The client never merges invented rows, never re-inserts rows the
server dropped, never owns cache key space, never knows why a row is flagged.

---

## 2. Where the expiry flag lives

At the page cache, in `SetPage`:

```
store    = incoming page, Keys = fresh ++ (previous.Keys − fresh.Keys) with Expired = true, TtlSeconds = −1, ExpiresAt = null
```

The merge is in the cache because the cache is the only place that still holds
the previous page when the new one arrives — the client never saw the front of
the queue.

Properties pinned in `JsonRedisReadCacheTests`:
- The carried-over row keeps its sorted position at the end of the page.
- A key the server still reports comes back unflagged — `Pages_DropTheFlagWhenTheKeyComesBack`.
- Nothing invented: a key the cache never held cannot reappear.

`KeyLeaf` therefore carries `ExpiresAt` (absolute moment) and `Expired`.

## 3. Client responsibility (only six things)

1. Paint a page exactly as the server sends it. Display `fromCache` / `cached as
   of` as given — the server reports the truth, the client does not invent it
   (the bug `fromCache: base || next` never happens again).
2. Countdown TTL live, from `expiresAt` + `now`, every second. Two display
   modes: `H:M:S` (live countdown) and `Date` (absolute moment).
3. On arrival it does not know refresh timing; it calls `/keys` exactly like any
   navigation and paints the answer.
4. On `fromCache: true` it fires one background fetch with `?refresh=true`.
   It does **not** decide what is cached: the flag is the server's judgement.
5. A row the server says `expired` is struck through + dimmed + labelled
   `expired`. The client does not retry expired rows.
6. AutoRefresh is user-set (`10s / 15s / 30s / 60s`, default 30s). It is the
   backstop for keyspace movement and for pages the client is staring at. It
   does not decide freshness.

The client never:
- `??`-invents a `cachedAt` for a continuation page,
- restores a row the server dropped from an incoming page,
- decides "still alive" for itself.

## 4. Expiry recheck (POST `/api/databases/{id}/keys/probe`)

A countdown reaching zero is a wall-clock claim, not proof of absence — a writer
may have rewritten the key since the scan. So:

- The hook scans its own page ref once per second for keys with `expiresAt <= now()`.
- The ones not already re-checked are buffered for `PROBE_COALESCE_MS = 250`,
  hard-capped at `PROBE_MAX_WAIT_MS = 1000` (not debounced — a short-TTL
  workload never goes quiet).
- One `POST /keys/probe` carries at most `PROBE_BATCH_SIZE = 200` keys (500
  server-side).
- Answer `exists: false` → **gone** → the page it came from is refetched at
  once, ahead of the AutoRefresh tick, because the keyspace demonstrably moved.
  The refetch replaces the row, or carries it over again flagged `expired`.
- Answer `exists: true` → row takes the fresh `ttlSeconds` / `expiresAt` from
  the answer and the countdown restarts.

A refetch is one per page per flush, floored by `GONE_REFETCH_MIN_INTERVAL_MS = 1000`,
so a page whose keys all expire in the same second is asked once, not N times.

If the recheck cannot be reached, rows stay as they are; the next AutoRefresh
tick replaces the page wholesale.

## 5. Which path is live vs cache

| Read | Mode | Why |
|---|---|---|
| Redis `Scan` | cache-first, `?refresh=true` bypasses | a tree paints fast; `?refresh` for manual Root/Browse |
| Redis `ListDatabases` | cache-first, same | same |
| Redis `InspectAsync` | live-first, cache fallback only | the user asked about this key now |
| Redis `GET/POST /keys/probe` | live-only | the recheck is the trigger that replaces the stale row |
| DB schema / query | cache-first + background revalidate (2026-11-07) | unchanged |
| MCP | live-first | an agent does not want a snapshot masquerading as live |

## 6. What "gone" does not do

- It does not remove the row from the page. `SetPage` reinstates it flagged
  `Expired` because the cache remembers it.
- It does not block writes. Probes are reads.
- It does not guarantee the writer's intent: a key rewritten after the recheck
  comes back unflagged on the next page event.

## 7. Non-goals

- Redis pub/sub or keyspace notifications: out of scope here. If a future
  requirement demands sub-10s freshness for arbitrary keyspace events, the fix
  is a server-push on the `databases` SSE topic, not a faster poll.
- SQL depends on none of this.

## 8. Verify

`pg 86` (`redis-browser.test.ts`, `redis-tree.test.tsx`) and `149` (`Eling.Core.Tests`):
`Pages_DropsTheFlagWhenTheKeyComesBack`, `Pages_KeepAKeyThatLeftTheKeyspace_FlaggedExpired`,
`Pages_DoNotInventKeysTheCacheNeverSaw`, `useRedisBrowser expiry recheck`,
`useRedisBrowser gone rows`.

## 9. Open items

- The `gone` trigger that refetches the whole page could revalidate more narrowly
  (one prefix) if pages ever get large. Not needed for the current shape
  (137–220 keys, page size 200).
- `loadMoreRedis` merging still does request-level page composition — fine for
  pagination, not a cache mechanism, but it means the cache is not the sole
  merge point on that path. It does not invent rows; it just orders them.
