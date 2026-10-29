# DB Connection — Redis (Key-Value Browsing) Design Specification

- **Date:** 2026-10-26
- **Status:** Proposed (awaiting user review)
- **Target Projects:** `Eling.Core`, `Eling.Backend`, `tests/Eling.Core.Tests`, `tests/Eling.Backend.Tests`, `src/frontend/Eling.Dashboard`
- **Extends:** `docs/superpowers/specs/2026-10-13-db-connection-design.md` — this spec adds a third provider and a **non-SQL read path**. It does not change the SQL contract, the registry, the credential rules, or the audit trail. Everything in the parent spec that is provider-agnostic stays as written.

---

## 1. Overview & Scope

Redis becomes a registered connection in the existing `/api/databases` registry, browsed as a **lazy key tree** instead of a table schema. Keys are split on `:` into segments, so `user:1:email` appears as `user` → `1` → `email`. Every node expands on demand through one paged `SCAN`; nothing is fetched eagerly.

**In scope (this phase):**

- `DatabaseProvider.Redis` — a third provider in the existing enum, registry, dashboard form, and connection tree
- Redis connection string build/parse in `DatabaseConnectionStringFactory` (host, port, ACL user, password, db index)
- Connection test (`PING`) through the existing `POST /api/databases/{id}/test` route
- Lazy key tree with cursor paging: `GET /api/databases/{id}/keys`
- Per-key inspection (type, TTL, length, capped value preview): `GET /api/databases/{id}/keys/inspect`
- Read-only command console with an explicit whitelist: `POST /api/databases/{id}/keys/command`
- `RedisCommandSafety` — the shared guard, used by both the REST console and any later MCP tool
- A bounded `ConnectionMultiplexer` pool (Redis multiplexers are long-lived; see §2.3)
- MCP: `redis_scan`, `redis_inspect`. `db_list_connections` picks Redis up for free; `db_get_schema` and `db_query` reject it
- Dashboard: provider option, db-index column, tree panel replacing the schema tab for Redis connections, value viewer
- Audit entries for every key read and every console command

**Out of scope (explicitly):**

- Any mutation — no `SET`, `DEL`, `EXPIRE`, `FLUSHDB`. `POST /{id}/mutate` stays SQL-only.
- `redis_command` over MCP (see §4.3)
- Redis Cluster and Sentinel (rejected with a clear error, see §7.3)
- `KEYS` — never issued, in any code path
- Pub/sub, keyspace notifications, Lua scripting, stream entry editing, `MONITOR`, `CONFIG`, `DEBUG`
- Persisted key-tree cache (a stale tree is worse than a slow one)

### 1.1 Why Redis is not an `IDatabaseAdapter`

The obvious shape — a third adapter implementing `IDatabaseAdapter`, `GetSchemaAsync` returning the key tree, `QueryAsync` parsing Redis commands — was **rejected**. Two reasons, both structural:

1. **Schema is fetched whole.** `GET /api/databases/{id}/schema` returns the complete schema in one response and the dashboard caches it. For SQL that is fine: a schema is small and static. For Redis the "schema" is the entire keyspace, so opening a connection would force a full sweep — `SCAN` to exhaustion — which is exactly what Redis documentation warns against on production servers. A tree that must be complete before it can be rendered cannot be lazy.
2. **A key is both leaf and node.** `user:1` (a string) and `user:1:email` can coexist. A table model has one name per table; a tree needs both rows. Forcing the tree into `TableSchema` produces a fake table with zero columns and loses the collision.

So `IDatabaseAdapter` stays SQL-only. The Redis read path is its own interface, and the SQL endpoints reject Redis by provider rather than degrading into empty results. This costs one new service and saves a fabricated contract that every future provider would have to lie about.

---

## 2. Architecture & Components

### 2.1 New and edited files

```
Eling.Core/Database/
  DatabaseProvider.cs                  (edit) append Redis
  DatabaseConnectionStringFactory.cs   (edit) Build/Parse for Redis
  RedisCommandSafety.cs                (new)  read-only command whitelist
  KeyTreeSegmenter.cs                  (new)  pure: key + prefix -> (nodes, leaves)
  IRedisKeyReadService.cs              (new)  key tree, inspect, command port
  RedisKeyReadService.cs               (new)  SCAN paging, TYPE/TTL, value previews
  RedisConnectionPool.cs               (new)  bounded ConnectionMultiplexer cache

Eling.Backend/
  Dtos/DatabaseKeyDtos.cs              (new)  request/view DTOs for the key routes
  Endpoints/DatabaseKeyEndpoints.cs    (new)  maps onto the existing /api/databases group
  Mcp/Tools/RedisScanTool.cs           (new)  MCP redis_scan
  Mcp/Tools/RedisInspectTool.cs        (new)  MCP redis_inspect
  Mcp/ServerInstructions.cs            (edit) describe the two redis tools
  Bootstrap/DashboardServices.cs       (edit) DI: pool + service
  Bootstrap/DashboardRoutes.cs         (edit) app.MapDatabaseKeyEndpoints();

src/frontend/Eling.Dashboard/src/app/dashboard/databases/
  Databases.tsx                        (edit) provider option, Redis branch in the right panel
  KeyTree.tsx                          (new)  lazy tree, paging, filter box
  KeyInspector.tsx                     (new)  type/TTL/length + capped value viewer
```

`StackExchange.Redis` **3.4.20** (latest stable as of 2026-10-26; `4.x` is alpha only) is added to `Eling.Core.csproj`. It ships a real `net10.0` asset, so no downlevel shims.

### 2.2 Layers

| Layer | Knows about | Does not know |
|---|---|---|
| `DatabaseEndpoints` (existing) | SQL providers | Redis |
| `DatabaseKeyEndpoints` (new) | provider check → `IRedisKeyReadService` | SQL, adapters |
| `RedisKeyReadService` | `IServer`, `IDatabase`, pool | HTTP, MCP, DTOs |
| `IDatabaseAdapter` | SQLite, MySQL | Redis |

Both endpoint files map routes onto the **same** `MapGroup("/api/databases")`; the split is file size, not URL shape. `DatabaseEndpoints.cs` is already 611 lines and the key routes add roughly the same again.

### 2.3 Connection lifetime — the one place Redis differs from SQL

SQL adapters open, use, and close a connection per operation (parent spec §2). Redis must not: `ConnectionMultiplexer` is thread-safe, designed to be shared for the process lifetime, and its construction costs a TCP handshake plus `HELLO`/`AUTH` on every endpoint in the topology. Creating one per tree click would pay that on every expand.

`RedisConnectionPool` is a singleton:

```csharp
public interface IRedisConnectionPool
{
    Task<IConnectionMultiplexer> GetAsync(DatabaseConnection connection, CancellationToken ct = default);
    void Invalidate(string connectionId);
    ValueTask DisposeAsync();
}
```

- Keyed by `connection.Id`; each entry stores the connection string it was built from. A stored connection whose string changed (edit, password rotation) is rebuilt on next use — `Invalidate` is also called from `PUT` and `DELETE` in `DatabaseEndpoints`.
- Built with `AbortOnConnectFail = false`, `ConnectTimeout = 5000`, `SyncTimeout = 5000`, `ClientName = "eling-dashboard"`, `AllowAdmin = false`. `AbortOnConnectFail = false` keeps the multiplexer alive through a blip; the **test** path therefore calls `PingAsync` after connecting, so "connected" means a real round trip, not a background retry loop.
- Idle entries are evicted after 5 minutes (`lastUsed` touched on `GetAsync`) by the same timer that prunes nothing else — a single `System.Threading.Timer` inside the pool. At most `maxConnections` (16) multiplexers are held; beyond that the least recently used is disposed.

---

## 3. Data Model & Storage

### 3.1 Provider

```csharp
public enum DatabaseProvider
{
    Sqlite,   // 0
    MySql,    // 1
    Redis     // 2  -- append only; existing rows keep 0/1
}
```

`JsonDatabaseConnectionStore` writes enums as numbers (`JsonDefaults.Shared` has no string-enum converter), so **Redis must be appended last and never reordered**. No migration is needed; old files keep loading.

### 3.2 Connection string

Redis is stored like the others: one opaque provider connection string in `databases.json`, plaintext at rest, never returned by the API (parent spec §3.3).

- **Manual fields** (`ManualConnectionFields`) map as: `Host` + `Port` (default 6379) → endpoint; `Username` → ACL `User` (null = default user); `Password` → `Password`; `Database` → `DefaultDatabase` as an integer 0–15.
- **Build** must set properties on `ConfigurationOptions` and let `options.ToString()` render it — never concatenate, so a password containing `,` or `=` survives. `Parse` runs `ConfigurationOptions.Parse` and reads the fields back for form pre-fill.
- **TLS** is expressible only in raw connection-string mode (`ssl=true,sslHost=...`) in v1. A `UseSsl` checkbox on the manual form is a follow-up (§11); `ManualConnectionFields` is shared with SQLite/MySQL and does not grow for this phase.
- A stored string that `Parse` cannot read returns `null`, exactly as SQL does — the form then shows empty fields instead of guessing.

### 3.3 Key model (wire, not storage)

Nothing about the keyspace is persisted. The shapes below exist only in responses.

```csharp
public sealed record KeyNode(string Segment, string Prefix);          // prefix ends with ':'
public sealed record KeyLeaf(string Key, string Type, long TtlSeconds); // TtlSeconds: -1 no expiry
public sealed record KeyPage(
    string Prefix,
    IReadOnlyList<KeyNode> Nodes,
    IReadOnlyList<KeyLeaf> Keys,
    long Cursor,
    int PageOffset,
    bool Complete,
    int Scanned);
```

`Type` is the lowercase Redis type name (`string`, `list`, `set`, `zset`, `hash`, `stream`, `none`). Fetching it per leaf is one `TYPE` per key; leaves are pipelined in one `IBatch` (§5.4).

---

## 4. API Surface

### 4.1 Routes

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/api/databases/{id}/keys?database=0&prefix=&cursor=0&pageOffset=0&pageSize=200` | one page of the tree |
| `GET` | `/api/databases/{id}/keys/inspect?database=0&key=user:1` | type, TTL, length, capped value |
| `POST` | `/api/databases/{id}/keys/command` | read-only console: `{ database, command, args[] }` |

Notes that are not negotiable:

- **`key` and `prefix` travel in the query string, never as path segments.** Redis keys may contain `/`, `:`, `*`, spaces, and `%`. A key containing `/` would otherwise split the route; a key containing `%2F` would round-trip differently depending on the proxy. Query-string binding handles all of it.
- `pageSize` is clamped to **1–500** (default 200). `cursor` and `pageOffset` are the two values `IScanningCursor` reports back; the client echoes both (§5.3).
- Every route rejects a non-Redis connection with **400** and a message naming the provider. `/{id}/schema` and `/{id}/query` reject Redis the same way, symmetrically. A client that guesses the wrong route gets a sentence, not an empty `200`.

### 4.2 `GET /{id}/keys`

```json
{
  "prefix": "user:",
  "nodes": [ { "segment": "1", "prefix": "user:1:" } ],
  "keys":  [ { "key": "user:1", "type": "hash", "ttlSeconds": 3600 } ],
  "cursor": 1234,
  "pageOffset": 0,
  "complete": false,
  "scanned": 200
}
```

- **Root** (`prefix` empty) is **not** a key page: it is the list of databases that hold keys, from `INFO keyspace` (`db0:keys=…,expires=…`). Nodes are `{ segment: "0", prefix: "" }` with the database index carried by the `database` query parameter, so the dashboard renders `db0 (12,431 keys)` before anyone scans anything. `DBSIZE` for the selected database is returned in the same call.
- `complete: false` means *this page ended mid-scan* — call again with the returned `cursor`/`pageOffset` to continue the **same** prefix. `complete: true` means the scan for that prefix finished.
- A page can return fewer than `pageSize` items with `complete: false`: items are counted after segment de-duplication, so many scanned keys may collapse into one node.
- `scanned` is the number of keys the server actually returned, for the UI's "scanned N keys" line. It is the honest measure of cost.

### 4.3 `GET /{id}/keys/inspect`

```json
{
  "key": "user:1",
  "type": "hash",
  "ttlSeconds": 3600,
  "expiresAt": "2026-10-26T13:04:11Z",
  "length": 3,
  "encoding": "listpack",
  "preview": { "kind": "hash", "entries": [ { "field": "email", "value": "a@b.c" } ], "truncated": false }
}
```

- One `TYPE` first; a missing key (`RedisType.None`) answers **404** with `{ "error": "key not found" }` — not an empty 200.
- `ttlSeconds` is `-1` for "no expiry" (`KeyTimeToLiveAsync` returns `null` for both *no expiry* and *missing*, which is why `TYPE` runs first). `expiresAt` is `now + ttl` computed from the backend process clock. `PEXPIRETIME` would remove that clock skew, but the dev server rejects it (Redis before 7), so the field is approximate by design; a client that cares compares `ttlSeconds`.
- `length` is per type: `STRLEN`, `LLEN`, `HLEN`, `SCARD`, `ZCARD`, `XLEN`.
- **Preview caps:** 100 entries or 64 KB per response, whichever comes first, then `truncated: true`. A 50 MB string returns its first 64 KB and says so. Sets and sorted sets are paged with `SScan`/`ZScan` rather than `SMEMBERS`/`ZRANGE 0 -1`, so a million-member set does not materialize in memory.
- `encoding` comes from `OBJECT ENCODING` (read-only, cheap) — it is the single most useful thing for diagnosing a memory question.

### 4.4 `POST /{id}/keys/command`

```json
{ "database": 0, "command": "HGETALL", "args": ["user:1"] }
```

Response: `{ "kind": "array", "value": ["email", "a@b.c"] }` where `kind` is `scalar` | `array` | `nil`. Executed through `IDatabase.ExecuteAsync(command, args)`, which returns `RedisResult` — no command-specific parsing.

Only commands on the whitelist run (§6). Anything else is **400** with `{ "error": "command not allowed: SET" }` before a connection is even opened. This route is read-only by construction, not by convention: there is no `mutate` flag, and `AllowAdmin` is `false` on the multiplexer.

### 4.5 MCP

| Tool | Status |
|---|---|
| `db_list_connections` | unchanged; Redis rows appear automatically (shared registry) |
| `db_get_schema` | **rejects** Redis: "connection 'X' is a Redis key-value store; use redis_scan" |
| `db_query` | **rejects** Redis: same message, no SQL classification attempted |
| `redis_scan` | **new** — `{ id, database?, prefix?, cursor?, pageOffset?, pageSize? }` → `KeyPage` |
| `redis_inspect` | **new** — `{ id, database?, key }` → inspect payload |
| `redis_command` | **not added this phase** |

`redis_scan` defaults to `pageSize: 200` and is hard-capped at 500, so an agent cannot turn a tool call into a keyspace sweep in one request. `redis_inspect` is read-only and value-capped by the same 64 KB rule, which keeps a tool result from blowing a context window.

---

## 5. Key Tree Semantics

This is the part that has to be exactly right; everything else is plumbing.

### 5.1 Segmentation

Given prefix `P` and a scanned key `K` that starts with `P`:

1. `rest = K.Substring(P.Length)`.
2. If `rest` contains `:` at index `i` → the **first segment** is `rest[..i]`, and the node prefix is `P + rest[..i] + ":"`.
3. Otherwise `K` is a **leaf** under `P`.

`user:1:email` under `P=""` → node `user`, prefix `user:`. Under `P="user:"` → node `1`, prefix `user:1:`. Under `P="user:1:"` → leaf `user:1:email`.

Edge cases, all covered by unit tests:

- **Leaf and node at the same name.** `user:1` and `user:1:email` both exist → under `P="user:"` the response carries node `1` **and** leaf `user:1`. This is why the tree cannot be a table (§1.1).
- **Trailing colon.** Key `user:` under `P=""` → node `user`, prefix `user:`; under `P="user:"`, `rest` is empty → empty segments are **never** nodes, so `user:` is a leaf. (It is also matched by `SCAN MATCH user:*`, which is why it shows up at all.)
- **Empty prefix segment.** `:foo` yields segment `""` under `P=""`; treated as a leaf `:foo`, never a node.
- **No colon at all.** `session` is a leaf at the root.
- **Binary-safe.** Segments are raw bytes rendered as UTF-8; a key that is not valid UTF-8 is shown with replacement characters in `Segment` while `Prefix`/`Key` stay exact. The client always sends `Prefix`/`Key` back, never `Segment`.

### 5.2 Scan, never `KEYS`

`IServer` in StackExchange.Redis has **no `Scan` member**. Cursor paging is expressed through `Keys`/`KeysAsync`:

```csharp
IAsyncEnumerable<RedisKey> KeysAsync(
    int database = -1, RedisValue pattern = default,
    int pageSize = 250, long cursor = 0, int pageOffset = 0,
    CommandFlags flags = CommandFlags.None);
```

The library picks `SCAN` over `KEYS` based on the server version (2.8+ always scans). The enumeration is `IScanningCursor`: after consuming a page, `Cursor` and `PageOffset` give the resume point. We call it with `pattern = P + "*"` and `pageSize = requested pageSize`, and we stop the enumeration ourselves as soon as the item budget or the time budget is hit (§5.5) — leaving the cursor mid-stream is expected, not an error.

`SCAN MATCH` filters **server-side but still walks the whole keyspace**, so a deep node is not cheaper on the server than a full scan. What paging saves is memory, transfer, and latency — not server work. This is stated in the UI ("scanned N keys") rather than hidden.

### 5.3 Paging contract

`cursor` and `pageOffset` are echoed verbatim; the server keeps **no** per-client state. They are only meaningful together, and only for the prefix they were issued under. A client that changes `prefix` must reset both to `0`.

Because `SCAN` guarantees nothing about stability, the same key can appear on two pages while writes are in flight. The client de-duplicates by `Key`/`Prefix` when appending, so a duplicate is a no-op rather than a duplicated row. A key deleted mid-scan simply never appears.

### 5.4 Type and TTL for leaves

One `TYPE` and one `TTL` per leaf would be 2N round trips. Instead, the leaves of a page are pipelined in a single `IBatch` (`KeyTypeAsync`, `KeyTimeToLiveAsync`), so a 200-key page costs one scan plus one pipelined batch. `RedisType.None` (key expired between scan and batch) drops the leaf from the page rather than rendering a phantom row.

### 5.5 Budgets

Per request, whichever comes first:

- **item budget:** `pageSize` nodes+leaves returned;
- **scan budget:** 2000 keys scanned;
- **time budget:** 3000 ms wall clock.

Hitting any of them returns `complete: false` with the resume cursor. The time budget exists because a `MATCH` against a huge keyspace can take seconds before returning its first page; the endpoint must not hold an HTTP request open for a minute.

---

## 6. Read-Only Command Guard (`RedisCommandSafety`)

A static class in `Eling.Core/Database`, deliberately shaped like `SqlSafety` (same namespace, same "classify, don't execute" role, same reuse from REST and MCP):

```csharp
public enum RedisVerdict { Allowed, Rejected }

public sealed record RedisClassification(RedisVerdict Verdict, string Command, string? Reason);

public static class RedisCommandSafety
{
    public static RedisClassification Classify(string? command, IReadOnlyList<string>? args);
}
```

Rules:

1. `command` is trimmed, upper-cased, and matched **exactly** against the whitelist. No prefix matching, no aliases — `CONFIG` and `CONFIG GET` are both rejected, because `CONFIG SET` is one token away.
2. Whitelist (read-only, non-blocking, non-admin):
   `GET MGET STRLEN GETRANGE TYPE TTL PTTL EXISTS`
   `HGET HMGET HGETALL HKEYS HVALS HLEN HEXISTS HRANDFIELD`
   `LRANGE LLEN LINDEX LPOS`
   `SMEMBERS SCARD SISMEMBER SRANDMEMBER SSCAN`
   `ZRANGE ZRANGEBYSCORE ZREVRANGE ZCARD ZSCORE ZCOUNT ZSCAN`
   `XRANGE XREVRANGE XLEN XINFO`
   `SCAN DBSIZE PING ECHO TIME RANDOMKEY`
   `MEMORY USAGE OBJECT ENCODING OBJECT IDLETIME OBJECT FREQ`
   `INFO`
3. Explicitly denied even though they are "reads": `KEYS` (unbounded), `MONITOR`, `SUBSCRIBE`/`PSUBSCRIBE`/`SSUBSCRIBE`, `PSYNC`/`SYNC`, `WAIT`, `CLIENT`, `CLUSTER` (topology writes exist), `COMMAND`, `LATENCY`, `SLOWLOG`, `DEBUG`, `CONFIG`, `ACL`, `SCRIPT`, `FUNCTION`, `EVAL`/`EVALSHA`/`FCALL`, `SHUTDOWN`, `FLUSHALL`/`FLUSHDB`, `SWAPDB`, `MIGRATE`, `RESTORE`, `DUMP` (payload exfiltration), `BITFIELD` (`SET`/`INCRBY` sub-ops), `GETDEL`/`GETEX` (write).
4. `INFO` is allowed but its sections are filtered to `server`, `clients`, `memory`, `persistence`, `stats`, `replication`, `keyspace` — the response never carries `modules`, and never a `requirepass` echo.
5. The multiplexer sets `AllowAdmin = false`, so even a guard bug cannot reach `CONFIG`/`SHUTDOWN`. The guard is the message; the client flag is the wall.
6. Argument sanity: at most 16 args, each at most 512 bytes, no `\0`. `SCAN` via the console is rewritten to the service's own paging path rather than executed raw, so `COUNT 100000000` cannot be requested.

Every rejection is audited with `Outcome = Denied` and the offending command, mirroring the SQL path.

---

## 7. Error Handling & Guardrails

### 7.1 Audit

Two new actions in `DatabaseAuditActions`, spelled like the MCP tools per the existing convention:

```csharp
public const string Keys = "db_keys";                 // page reads
public const string InspectKey = "db_inspect_key";
public const string RedisCommand = "redis_command";
```

Every key page, inspection, and console command writes an `AuditEvent` with `Category = Database`, `Target = connection id`, `Query = prefix / key / command + args`, `ResultCount`, `DurationMs`, `Outcome`. Denied commands use `Outcome.Denied` and carry the reason. This means the audit trail shows `SCAN user:*` exactly as it happened — no summarization.

### 7.2 Failures

| Situation | Response |
|---|---|
| Connection unreachable | `503` `{ error, code: "redis_unavailable" }` — same posture as `DatabaseUnavailableException` for SQL. **The keyspace itself is never cached** (a stale tree is a lie); the database list and single-key inspections fall back to `IRedisReadCache` (24 h, `fromCache` + `cachedAt`) which the dashboard shows as "as of". |
| Wrong provider on a key route | `400` `"connection 'X' is a SQLite connection; key browsing requires Redis"` |
| Redis on `/schema` or `/query` | `400` `"connection 'X' is a Redis key-value store; use /keys"` |
| Missing key on inspect | `404` `"key not found"` |
| Denied command | `400` `"command not allowed: SET"` |
| Auth failure (`NOAUTH`/`WRONGPASS`) | `502` `"authentication failed"` — password never echoed, never logged |
| TLS required but not configured | `502` with the server's message, truncated to 200 chars |
| `pageSize` out of range | clamped, not rejected |
| Prefix longer than 1024 bytes | `400` |
| Time budget hit | `200` with `complete: false` — not an error; the client continues |

### 7.3 Cluster and Sentinel

Rejected, explicitly, at both test and first-use:

```
Redis Cluster is not supported in this phase: key scanning only sees one
node's slots, so the tree would silently be incomplete. Register a
standalone Redis instance instead.
```

Detection is `IServer.ServerType` (`Cluster` / `Sentinel` → reject; `Standalone` → proceed). Silent partial results are worse than a refusal, and a half-tree that looks complete is the failure mode that would waste hours later.

### 7.4 Frontend guardrails

- The tree renders the "scanned N keys" line on every page and a spinner while a page loads. Users learn the cost from the UI, not from a doc.
- Expanding a node appends; "load more" continues the same prefix with the returned cursor. Collapsing keeps the loaded page in memory for the session (a `Map<prefix, KeyPage>`), so re-expanding does not re-scan.
- `DBSIZE` is shown per database before any expansion, so scale is visible up front.
- The SQL tab is hidden for Redis; the console tab is shown instead, with the whitelist as a placeholder hint.
- The delete-connection dialog names the connection — deleting a Redis connection also disposes its multiplexer (§2.3).

---

## 8. Testing Strategy

**`tests/Eling.Core.Tests`**

- `KeyTreeSegmenterTests` — table-driven over the §5.1 cases: nested keys, leaf+node collision, trailing colon, leading colon, no colon, prefix that is itself a key, UTF-8 and replacement-character keys.
- `RedisCommandSafetyTests` — every whitelisted command allowed; every denied command rejected with a reason; case/whitespace handling; `CONFIG` vs `CONFIG GET`; arg-count and arg-size limits; `SCAN` never executed raw.
- `RedisConnectionStringFactoryTests` — build/parse round trip for host/port/user/password/db; password containing `,` `=` `;`; missing port defaults to 6379; unparseable string returns `null`; `Database` outside 0–15 rejected.
- `DatabaseProviderTests` — Redis is `2`; appending did not renumber existing members.

**`tests/Eling.Backend.Tests`**

- `DatabaseKeyEndpointsTests` with a fake `IRedisKeyReadService`: paging echoes cursor/pageOffset; prefix reset; `pageSize` clamping; 400 for SQL providers; 404 for a missing key; denied command returns 400 and writes a `Denied` audit event; every success writes a `Success` audit event with the prefix.
- `DatabaseProviderRoutingTests` — `/schema` and `/query` on a Redis connection return 400; `/keys` on SQLite/MySQL returns 400; `DatabaseAdapterFactory` never receives a Redis connection (a contract test that fails loudly if someone later adds a Redis adapter).
- `RedisPoolTests` — same id returns the same multiplexer; changed connection string rebuilds; `Invalidate` disposes; LRU eviction at the cap.

**Integration (opt-in, `ELING_REDIS_TEST=1`)**

- A dockerized Redis 7: seed 5000 keys across prefixes, assert the tree pages to completion without duplicates or omissions, assert a 1000-member hash previews 100 entries with `truncated: true`, assert `SET` is refused.

**Frontend**

- `KeyTree.test.tsx` — renders nodes and leaves from a stubbed page; expanding appends and does not duplicate on cursor overlap; "load more" preserves order; a leaf that is also a node renders both rows.

---

## 9. Deferred Items (flagged, not removed)

1. **Mutations** — `SET`/`DEL`/`EXPIRE`, batch delete, TTL editing. Would need its own approval gate and a `RedisMutationSafety`, exactly as SQL mutation does. Not this phase.
2. **`redis_command` over MCP** — the console exists over REST only. An agent-facing command surface is a wider blast radius than the two read tools, and nothing currently needs it.
3. **Cluster/Sentinel** — needs per-node scanning and slot-aware cursors, i.e. a different paging model, not a flag.
4. **`childCount` per node** — a real count requires exhausting the prefix; an estimate (`SCAN` sample × ratio) is a lie in a UI. Omitted rather than approximated.
5. **`SCAN TYPE`** — Redis 6+ filters by type server-side; worth adding once the tree has a type filter in the UI.
6. **Key tree cache** — deliberately none: `SCAN` results go stale within seconds on a live server. `IRedisReadCache` caches only the bounded, stable answers (database list, single-key inspection) with a 24 h expiry and a visible "as of" stamp; it is never the key tree.
7. **TLS checkbox on the manual form** — v1 requires raw connection-string mode for `ssl=true`.
8. **Value download / export** for large values — v1 truncates at 64 KB.
9. **Keyspace notifications / pub-sub inspector** — separate feature, separate connection semantics.

---

## 10. Decision Log

| # | Decision | Rationale | Rejected alternative |
|---|---|---|---|
| D1 | Redis is a provider, not an adapter | The key tree is not a schema and a key is both leaf and node; `GetSchemaAsync` must be complete and eager | **Option A:** fake `IDatabaseAdapter` with the tree as schema. Rejected in §1.1. **Option B:** separate Redis registry/file. Rejected: duplicates credentials, audit, and cache, and hides Redis from the one place people look. |
| D2 | Lazy tree over `:` segments, paged by `SCAN` | Opening a connection must not sweep the keyspace | Flat key list (unusable past a few thousand keys); `KEYS` (blocks the server) |
| D3 | `cursor` + `pageOffset` returned as two numbers, not an opaque token | They are exactly what `IScanningCursor` reports; encoding them into a string adds a parse step and a failure mode with no benefit | Base64-packed opaque cursor |
| D4 | `key`/`prefix` in the query string | Redis keys contain `/`, `:`, `*`, spaces; path segments need escaping that proxies and browsers disagree on | Path segments (`/keys/{key}`) |
| D5 | Bounded multiplexer pool keyed by connection id | Multiplexer construction is expensive and it is designed to be shared; per-request creation pays a handshake per click | Per-request `ConnectionMultiplexer` (simple, slow, socket churn) |
| D6 | Cluster/Sentinel refused | `IServer` scanning sees one node's slots; a partial tree that looks complete is the worst outcome | Silent best-effort scan |
| D7 | Whitelist guard in `Eling.Core`, shared by REST and MCP | Same shape as `SqlSafety`; one place to audit, one place to test | Per-endpoint inline checks |
| D8 | No `redis_command` over MCP | Read tools cover the agent use case; a general command surface is a wider blast radius with no current demand | Ship it behind the same whitelist |
| D9 | Root level is the database list (`INFO keyspace`) | Redis has 16 logical databases; hiding them makes `DBSIZE` and cross-db keys unexplainable | Root = keys of the default database only |
| D10 | `AllowAdmin = false` on every multiplexer | A guard bug then cannot reach `CONFIG`/`SHUTDOWN`; defence in depth, zero cost | Trust the whitelist alone |

---

## 11. Open Follow-Ups

- **TLS in the manual form** — add `UseSsl` to `ManualConnectionFields` (touches the shared record; do it once, for all providers, or not at all).
- **Node counts** — if a cheap estimate is ever acceptable, label it "≈" in the UI and never in an API field named `count`.
- **Type filter** — `SCAN TYPE string` on Redis 6+, plus a filter chip in the tree.
- **Per-connection scan budget** — a stored `maxKeysScanned` so a production connection can be capped lower than 2000.
- **Memory column** — `MEMORY USAGE` per leaf is one round trip each; only viable as an on-demand column, not in the page payload.
- **Multi-endpoint (replica read)** — `ConfigurationOptions.EndPoints` supports it; deciding which node answers `SCAN` is a correctness question, not a config flag.
