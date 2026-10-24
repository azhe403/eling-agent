# Eling Database Read Cache — Specification

**Date:** 2026-10-22
**Status:** Draft (Awaiting User Review)
**Scope:** `Eling.Core` (cache store, read service, adapters), `Eling.Backend` (DI, HTTP endpoints, MCP tools, server instructions, audit event), `Eling.Dashboard` (databases page), test suites

---

## 1. Overview & Goals

Eling registers database connections and reads from them: the dashboard browses
tables and runs SQL, and the agent queries the same connections through the
`db_query` / `db_get_schema` MCP tools. Every one of those reads goes straight
to the database. When the database is unreachable — the remote server is down,
the network is cut, the credentials were rotated — every read fails at once:
the dashboard shows a 502 and the agent gets "query failed" with nothing to
work with.

This specification adds a **backend-side cache of database reads**. When a read
cannot reach the database, the backend answers from its cache and says so:
the response carries `fromCache` and `cachedAt`, and the dashboard shows a
"cached" marker on whatever it is displaying. The agent sees the same flags in
the tool result.

The cache lives in the **backend**, not in the dashboard. The dashboard's
existing `localStorage` schema cache is removed as part of this work: a cache
that lives in one browser is invisible to the agent, is lost when the tab
closes, and cannot be shared by the two hosts that read the same database.

Goals:

1. **A dead database still answers.** Schema and rows read before the outage
   remain available to the dashboard and to the agent.
2. **Never pass stale data off as fresh.** Every cached answer is marked, with
   the age of the snapshot, in the API and in the MCP output.
3. **The cache follows the data.** A successful read refreshes the cache; a
   mutation, a connection edit, or a connection delete invalidates it.
4. **One implementation, both surfaces.** The HTTP endpoints and the MCP tools
   share a single read service, so the dashboard and the agent can never
   disagree about what "cached" means.

Non-goals:

- **Caching writes.** Mutations are never served from cache. When the database
  is unreachable, a write fails as it does today.
- **A background refresher.** The backend does not poll a dead database on a
  timer. Recovery is detected by the next read (§8).
- **Caching the connection form's "list databases" call.** That call runs
  against credentials that are not registered yet, so there is no connection to
  key a cache entry on.
- **Serving a stale snapshot past its maximum age.** Seven days is the ceiling
  (§4); past it the read fails as it does today.

---

## 2. Where the cache lives

**File:** `<CentralAgentDirectory>/database-cache.json`, beside the existing
`databases.json` registry. `CentralAgentDirectory.Resolve()` is what both
hosts already use for the registry (`DashboardServices.cs`,
`McpHostBuilder.cs`), so the dashboard's HTTP host and the MCP stdio host
share one cache file — the browser and the agent see the same snapshot.

Rationale for a file rather than an in-process dictionary: the moment a cache
matters most is often right after the backend restarted, and an in-memory
cache would be empty exactly then. A file also survives the backend being
replaced by an update while the database is still down.

**Format:** `JsonDefaults.Shared` (camelCase, indented, case-insensitive) —
the same serializer the registry uses. The shared instance is used as-is; the
cache adds no converter to it (see §3 for why none is needed).

**Write discipline:** temp file + `File.Move(temp, target, overwrite: true)`,
mirroring `JsonDatabaseConnectionStore.SaveCore`. A half-written cache file
must never replace a good one.

**Cross-process safety:** a named mutex (`Global\eling-db-read-cache`, its own
name — not the registry's) with a 5-second bounded wait, the same gate shape
`JsonDatabaseConnectionStore` uses. The MCP host and the dashboard host can
write the file at the same time.

**What the file must not contain:** connection strings, usernames, passwords,
host names, or any part of the connection string. Cache keys are built from
the connection **id** (a ULID), the database name, and a hash of the SQL
text; cache values are the schema and the rows the user already has access
to. A test pins this (§10).

---

## 3. What is cached

| Kind | Key | Value |
|---|---|---|
| Schema of a database | `schema\|{connectionId}\|{database}` | `DatabaseSchemaInfo` (tables, columns, FKs, indexes) |
| Schema of a server-level connection | `databases\|{connectionId}` | `DatabaseSchemaInfo` with `Tables` empty and `Databases` filled |
| Query result | `query\|{connectionId}\|{database}\|{maxRows}\|{sha256(sql)}` | `QueryResult` (columns, rows, truncated) |

The SQL text is hashed rather than stored, so a long statement does not bloat
the file and so the file does not become a log of what was queried.

The database name is part of the schema key (as it already is in the
dashboard's own cache key), so two databases on the same server never share a
schema entry.

**One underlying read, one entry.** A server-level connection's schema read
and the database list it carries are the same read, so they share the
`databases|` entry — there is no second copy that could disagree with the
first. The read service owns that branching (which read it is), so the HTTP
endpoint and the MCP tool cannot take different paths.

**Query results are keyed by the exact statement.** A statement that was never
run has no cached result and still fails while the database is down. This is
the honest limit of a snapshot-of-what-was-queried cache, and it is why the
marker matters more than the fallback itself.

**Cell values.** Rows are cached as JSON. A cell that was a `long` comes back
as a `long`, not as a `JsonElement`, so code that inspects values (the
dashboard's row-count check, for one) keeps working after a cache reload.
This needs no custom serializer: the cache entry record stores its cells as
`JsonElement`, and a small helper (`JsonCell.ToClrValue`) unwraps them into
`null` / `bool` / `long` / `double` / `string` on the way out. A provider
value with no natural JSON form (a `DateTime`, a `decimal`, a byte array) is
normalized to its JSON form on the way into the cache — a narrowing of what a
cached cell can be, and only cached reads are affected.

---

## 4. Freshness

Two thresholds, not one:

- **Stale after 24 hours.** An entry older than a day is still served when the
  database is unreachable, but it is marked as *stale* (§8) so the user knows
  the snapshot is aging. The database is often down for longer than a working
  day, and a schema that is three days old still tells the user what the
  tables look like — but only if it is clearly labelled.
- **Expired after seven days.** An entry older than a week is not served at
  all; it is pruned and the read fails as it does today. Seven days is the
  ceiling: past it the snapshot is too old to be useful.

So the lifecycle of a cached entry is:

| Age | Served? | Marker |
|---|---|---|
| 0 – 24 h | yes | "cached · 3 min ago" (amber) |
| 24 h – 7 d | yes | "stale · 3 days ago" (stronger tone) |
| > 7 d | no — pruned | (none; the read fails) |

Entries are pruned when the cache is read and when it is written, so the file
does not accumulate dead snapshots.

**Query entries are capped per connection** (the most recent 200) so that a
session that runs many distinct statements cannot grow the file without
bound.

---

## 5. When the fallback triggers

The fallback answers a read with cached data **only when the database could
not be reached**. A statement that is wrong — a syntax error, a column that
does not exist — is a mistake in the request, and answering it with yesterday's
rows would be worse than an error.

To tell those apart, the adapters get one new failure type:

**`DatabaseUnavailableException`** (new, `Eling.Core.Database`). The rule is
mechanical: an adapter wraps the failure of **opening the connection** in this
exception; a failure while **executing the statement** propagates unchanged.
`OpenAsync` covers the unreachable host, the wrong port, a rotated password,
and a missing database; a command failure does not.

The exception's message names the failure category only ("database
unavailable"), never the provider's own message, which can carry the host name
and the user name.

**Read flow (the same for schema, query, and database list):**

1. Ask the adapter for the live answer.
2. **Success** → store it in the cache with the current timestamp → answer
   with `fromCache: false`.
3. **`DatabaseUnavailableException`** → look for a cached entry →
   - **hit** → answer with `fromCache: true` and `cachedAt: {entry timestamp}`;
   - **miss** → the read fails exactly as it does today.
4. **Any other failure** → no fallback; the read fails.

Step 1 is unconditional: the live database is always tried first. A successful
read *is* the refresh — there is no second code path that could let the cache
drift from the database.

**How long a dead host may hold a read.** A read against an unreachable host
must fail fast enough for the dashboard's poll to be usable. MySQL's default
connect timeout is 15 seconds, which is longer than the poll interval. The
MySQL adapter therefore sets `ConnectionTimeout` to **3 seconds** — but only
when the stored connection string does not set it itself, so a user whose
server genuinely needs longer can say so in the connection string. The
30-second command timeout is unchanged, so a slow *query* is not cut short;
only the *connect* phase is bounded. SQLite needs no equivalent — opening a
local file is immediate.

---

## 6. Response shape

`QueryResult` and `DatabaseSchemaInfo` each gain two init-only properties:

```csharp
public bool FromCache { get; init; }
public DateTimeOffset? CachedAt { get; init; }
```

Init-only, so the adapters' own return values are unaffected (both default to
`false` / `null`), and the read service sets them with a `with` expression.
Because the endpoints already return these records directly
(`Results.Ok(result)`), the JSON gains the two fields without any DTO
reshaping.

**The database list endpoint keeps its shape.** `POST /{id}/databases` returns
a bare `string[]` today, and it stays that way — no `DatabaseListResponse`,
no dashboard change for those two call sites. It reads through the service
like every other read, but it **bypasses the cache whenever the request
carries a body**: that body layers unsaved fields (a host the user just
typed) over the stored connection, so the answer is about a *different*
server than the registered one, and caching it under the connection's key
would let a later read of the registered connection be answered with another
server's database list. A bodyless request — the registered connection — uses
the cache.

The consequence is accepted deliberately: the tree's database list for a
server-level connection is served from the cache when the database is down,
but it carries no "cached" chip, because the list is an input to the tree
rather than something the user is reading. The tables under it, and the rows
under those, are the marked content.

**MCP output.** `db_query` returns `QueryResult` and `db_get_schema` returns
`DatabaseSchemaInfo`, so both tools carry the same two fields to the agent
without changing their return types. Both tool descriptions are updated to
say that a cached answer can come back when the database is unreachable, and
the server instructions get a matching entry (§9).

---

## 7. Invalidation

| Event | Effect |
|---|---|
| A successful read | That entry is replaced with the fresh answer |
| A successful mutation (`POST /{id}/mutate`) | All entries for that connection are dropped — the rows changed, and the statement may have been DDL that changed the schema |
| A connection edit (`PUT /{id}`) | All entries for that connection are dropped — the connection string may now point at a different server or database |
| A connection delete (`DELETE /{id}`) | All entries for that connection are dropped |
| An entry older than seven days | Dropped, and not served |

The mutation and edit rules are deliberately blunt: dropping a whole
connection's cache is cheap, and serving a schema that belongs to a different
database is not recoverable by the user.

---

## 8. The dashboard

**The `localStorage` schema cache is removed.** The keys
`db.schema.cache.{id}` and `db.schema.cache.{id}.{database}`, the
`schemaCacheKey` helper, and the stale-while-revalidate reads that use them
all go away. The explorer state (`db.explorer.state`) stays — that is the
selected table and the expanded tree, not data.

**The marker.** A chip in the card header of each tab that can show cached
data:

- **Data** tab — next to `Data - {table}`
- **Schema** tab — next to `Schema - {connection}`
- **SQL** tab — next to `Query - {connection}`

Its text and tone follow the entry's age (§4):

- 0 – 24 h: `cached · 3 min ago`, amber (warning tone) — stale data is a
  condition to notice, not an error.
- 24 h – 7 d: `stale · 3 days ago`, a stronger tone (orange/red) — the
  snapshot is aging and the user should know.

A live read shows no chip at all. The chip always carries a tooltip with the
exact `cachedAt` timestamp.

**Every write affordance is disabled while cached rows are on screen** — the
inline cell editor and its Apply, the per-row delete button, "+ Add row", and
the context menu's "Set now / Set null / Set empty string". One flag
(`browseResult.fromCache`) gates all of them. The rationale is uniform: a
write cannot succeed while the database is unreachable, and a failed write
would look like a bug in the app rather than an unreachable database.

**Auto-retry until live.** While the visible result is cached, the dashboard
re-reads the same thing in the background: every 5 seconds for the first
minute, then every 15 seconds, until a live answer arrives. The moment it
does, the view is replaced with live data and the chip disappears — with no
user action.

Two rules keep that loop safe:

- **The Data and Schema tabs poll; the SQL tab does not.** The SQL tab holds
  a statement the user typed, and the dashboard routes a statement that is not
  read-only to the *mutate* endpoint — a background poll that re-ran it would
  repeat the write every few seconds. The SQL tab shows the cached chip, and
  the user presses Run again. Its result is still cached and still marked; it
  simply does not re-run itself.
- **Polls never overlap.** The next poll starts only after the previous one
  has finished, so a slow (or dead) database cannot stack requests.

This is a client-side loop, not a backend one: the backend has no way to know
whether a browser is still watching, and a browser that is closed should not
keep the backend hammering a dead database.

**The failure path keeps its own retry.** The new poll only runs while a
cached result is on screen. A read that fails outright — no cached entry
exists yet — still gets the existing three attempts with backoff, so a
first-ever read against a database that is down does not leave the page
stuck in a permanent error.

---

## 9. Surfaces to touch

**`Eling.Core`**

| File | Change |
|---|---|
| `Database/DatabaseUnavailableException.cs` | **New.** The failure type that separates "cannot reach the database" from "the statement is wrong" |
| `Database/IDatabaseReadCache.cs` | **New.** The cache port: get, set, invalidate, prune |
| `Database/JsonDatabaseReadCache.cs` | **New.** The file-backed implementation |
| `Database/DatabaseReadService.cs` | **New.** The one read path (§5) used by both hosts |
| `Database/IDatabaseReadService.cs` | **New.** The service port |
| `Database/QueryResult.cs` | `FromCache` + `CachedAt` |
| `Database/DatabaseSchemaInfo.cs` | `FromCache` + `CachedAt` |
| `Database/Adapters/SqliteDatabaseAdapter.cs` | Wrap an `OpenAsync` failure in `DatabaseUnavailableException` |
| `Database/Adapters/MySqlDatabaseAdapter.cs` | Same; plus the 10-second connect timeout when the connection string sets none |
| `Serialization/JsonCell.cs` | **New.** The cell-value unwrapping helper (§3) |

**`Eling.Backend`**

| File | Change |
|---|---|
| `Bootstrap/DashboardServices.cs` | Register the cache and the read service |
| `Bootstrap/McpHostBuilder.cs` | Register the cache and the read service (MCP host) |
| `Endpoints/DatabaseEndpoints.cs` | Read through the service; invalidate on mutate/edit/delete |
| `Mcp/Tools/DatabaseQueryTool.cs` | Read through the service; take the read service as a required constructor argument; update the tool description |
| `Mcp/Tools/DatabaseGetSchemaTool.cs` | Same |
| `Mcp/ServerInstructions.cs` | A section entry for the cached fallback |
| `Core/Audit/AuditEvent.cs` | An optional `FromCache` flag, so a cache hit is visible in the audit trail (§12) |

**`Eling.Dashboard`**

| File | Change |
|---|---|
| `src/app/dashboard/databases/Databases.tsx` | Drop the `localStorage` cache; read `fromCache`/`cachedAt`; the chip; the auto-retry loop; every write affordance disabled while cached |

---

## 10. Tests

**`tests/Eling.Core.Tests/DatabaseReadCacheTests.cs`**

- A stored schema round-trips through the file.
- A stored query result round-trips, and a numeric cell comes back as a
  number (not a `JsonElement`).
- An entry older than the maximum age is not served.
- Invalidating a connection drops all of its entries and leaves other
  connections' entries alone.
- A corrupt cache file is ignored (an empty cache, a warning log) rather than
  throwing — the same rule the registry follows.
- The written file contains no connection string, user name, or password.
- A `Set` whose value is unchanged does not rewrite the file.

**`tests/Eling.Core.Tests/DatabaseReadServiceTests.cs`**

- A live read returns the live answer and refreshes the cache.
- An unavailable database with a cached entry answers from the cache, with
  `fromCache: true` and a `cachedAt`.
- An unavailable database with no cached entry fails.
- A failing statement (not an unavailable database) does **not** fall back to
  the cache.

**`tests/Eling.Backend.Tests/DatabaseReadCacheEndpointTests.cs`**

- `GET /api/databases/{id}/schema` answers `200` with `fromCache: true` when
  the database cannot be reached and a snapshot exists.
- `POST /api/databases/{id}/query` answers `200` with `fromCache: true` in the
  same situation.
- A mutation drops that connection's cached entries.

**`tests/Eling.Backend.Tests/DatabaseConnectionErrorTests.cs`** — updated: the
two MCP tools take the read service as a required constructor argument, so
these direct constructions are given one.

The dashboard has no test runner (only `lint` and `build`), so the frontend
change is verified with `pnpm lint` and `tsc --noEmit`.

---

## 11. Error handling

| Failure | Behavior |
|---|---|
| The database is unreachable and a cached entry exists | `200`, the cached content, `fromCache: true`, `cachedAt` set |
| The database is unreachable and no cached entry exists | `502` — the same failure as today |
| The statement is wrong | `400`/`502` — the same failure as today, never a cached answer |
| The cache file is corrupt | The cache is treated as empty; the read falls back to the live database |
| The cache file cannot be written | The read still succeeds; the failure is logged |
| A cached entry is older than seven days | Treated as no entry |

---

## 12. Decisions taken, and their reasons

| Decision | Reason |
|---|---|
| The cache is a file in the agent's data directory | It survives a backend restart, and both hosts already resolve that directory |
| The cache lives in the backend, and the dashboard's `localStorage` cache is removed | One cache, visible to the agent and to every browser, instead of one per browser |
| The fallback triggers only on an unreachable database | A wrong statement answered with old rows hides the mistake |
| Stale at 24 h, expired at 7 d | A day-old snapshot is still useful if clearly labelled; a week-old one is not |
| The connect phase is bounded at 3 seconds, the command phase is not | A dead host must fail faster than the poll interval; a slow query must not be cut short |
| Seven days as the maximum age | Long enough for the outages this exists for, short enough that the data still means something |
| The dashboard polls while cached, the backend does not | The backend cannot know whether anybody is watching; a closed browser should not keep the database being hit |
| The SQL tab does not poll | Its statement may be a mutation, and a poll would repeat the write |
| Every write affordance is disabled on cached rows | A write cannot succeed while the database is down |
| Invalidation is blunt — the whole connection | Cheap, and a schema from the wrong database is not recoverable by the user |
| The query key is the exact SQL text | The cache is a snapshot of what was actually read; it cannot answer a question it was never asked |
| The edit-form's database list bypasses the cache | It reads unsaved credentials — another server's list must never be cached under this connection |
| A cache hit is recorded in the audit trail | The trail is where an operator looks to see what the system actually did |
| `Set` skips the write when the value is unchanged | The dashboard polls; rewriting an identical file on every poll is pure cost |

---

## 13. Not in this change

- Caching mutation results.
- A backend-side poller that refreshes the cache on a timer.
- Caching the connection form's "list databases" call.
- Sharing a cache between connections that point at the same server.
- Per-connection cache settings in the dashboard (size, age, on/off).
