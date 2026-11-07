# Database Metadata Cache-First Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make schema and database-list reads cache-first on the dashboard surface so the tree paints instantly from `database-schema-cache.json`, with a background live refresh that replaces stale data silently.

**Architecture:** `DatabaseReadService.GetSchemaAsync` gains a `preferCache` flag (default `true`). When true and an unexpired cache entry exists, it returns immediately with `source` set to `"live"` (age < 30s) or `"refreshing"` (age >= 30s, background `Task.Run` fires). On `DatabaseUnavailableException`, source becomes `"offline"`. MCP's `DatabaseGetSchemaTool` passes `preferCache: false` to stay live-first. The HTTP endpoint adds `?refresh=true` to bypass cache for manual refresh.

**Tech Stack:** .NET 8+, C# records, xUnit, Next.js dashboard (React/TypeScript)

**Spec:** [docs/superpowers/specs/2026-11-06-db-metadata-cache-first-design.md](../specs/2026-11-06-db-metadata-cache-first-design.md)

## Global Constraints

- Freshness window: 30 seconds (hardcoded in `DatabaseReadService`)
- `fromCache` = `source != "live"` (backward compat for dashboard retry loop)
- MCP `db_get_schema` stays live-first (`preferCache: false`)
- `POST /{id}/databases` with body stays uncached (unsaved credentials)
- File cache format unchanged — no new fields in `database-schema-cache.json`
- Single-flight: one background refresh per `(connectionId, database)` at a time via `ConcurrentDictionary`
- Failed probe cooldown: 30 seconds before next background attempt

## Review Focus

1. A `?refresh=true` request must bypass cache even if cache is fresh — test pins the query param path in `DatabaseEndpoints.cs`
2. Background refresh exceptions must be swallowed (logged only) — test verifies no unobserved task exception when adapter throws
3. Two concurrent `GetSchemaAsync` calls for same key must produce exactly one background refresh — test uses `CountdownEvent` or counter
4. `ListDatabasesAsync` with server-level connection must also be cache-first — test covers the `ReadDatabaseListAsync` private path
5. Dashboard `ensureSchema(force: true)` must send `?refresh=true` — test verifies the fetch URL includes the param

---

### Task 1: Add `CacheSource` enum and TTL properties to `DatabaseSchemaInfo`

**Files:**
- Modify: `src/backend/Eling.Core/Database/DatabaseSchemaInfo.cs:27-37`
- Test: `tests/Eling.Core.Tests/DatabaseReadServiceTests.cs` (new file)

**Interfaces:**
- Consumes: nothing
- Produces: `CacheSource` enum, `Source`, `CacheExpiresAt`, `CacheTtlSeconds` properties used by Tasks 2-6

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void SchemaInfo_DefaultSource_IsLive()
{
    var info = new DatabaseSchemaInfo([]);
    Assert.Equal(CacheSource.Live, info.Source);
    Assert.False(info.FromCache);
    Assert.Null(info.CacheExpiresAt);
    Assert.Null(info.CacheTtlSeconds);
}

[Fact]
public void SchemaInfo_Refreshing_SetsFromCacheTrue()
{
    var expires = DateTimeOffset.UtcNow.AddHours(1);
    var info = new DatabaseSchemaInfo([]) { Source = CacheSource.Refreshing, CacheExpiresAt = expires, CacheTtlSeconds = 3600 };
    Assert.True(info.FromCache);
    Assert.Equal(expires, info.CacheExpiresAt);
    Assert.Equal(3600, info.CacheTtlSeconds);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Eling.Core.Tests --filter "SchemaInfo_DefaultSource_IsLive|SchemaInfo_Refreshing_SetsFromCacheTrue" --no-restore`
Expected: FAIL — `CacheSource` does not exist

- [ ] **Step 3: Add `CacheSource` enum and properties to `DatabaseSchemaInfo.cs`**

Add the enum in the same file above the record. Add three init-only properties. Change `FromCache` to computed:

```csharp
public enum CacheSource { Live, Refreshing, Offline }
```

Add to `DatabaseSchemaInfo`:
```csharp
public CacheSource Source { get; init; } = CacheSource.Live;
public DateTimeOffset? CacheExpiresAt { get; init; }
public long? CacheTtlSeconds { get; init; }
public bool FromCache => Source != CacheSource.Live;
```

Remove the old `public bool FromCache { get; init; }` line. Keep `CachedAt` as-is.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Eling.Core.Tests --filter "SchemaInfo_DefaultSource_IsLive|SchemaInfo_Refreshing_SetsFromCacheTrue" --no-restore`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/backend/Eling.Core/Database/DatabaseSchemaInfo.cs tests/Eling.Core.Tests/DatabaseReadServiceTests.cs
git commit -m "feat(db-cache): add CacheSource enum and TTL properties to DatabaseSchemaInfo"
```

---

### Task 2: Add `preferCache` parameter to `IDatabaseReadService.GetSchemaAsync`

**Files:**
- Modify: `src/backend/Eling.Core/Database/IDatabaseReadService.cs:17-20`
- Modify: `src/backend/Eling.Core/Database/DatabaseReadService.cs:70-108`
- Modify: `src/backend/Eling.Backend/Mcp/Tools/DatabaseGetSchemaTool.cs:51`
- Modify: `src/backend/Eling.Backend/Endpoints/DatabaseEndpoints.cs:328`

**Interfaces:**
- Consumes: `CacheSource` from Task 1
- Produces: updated `GetSchemaAsync` signature used by Tasks 3-5

- [ ] **Step 1: Update `IDatabaseReadService` interface**

Change the signature at line 17-20 to:
```csharp
Task<DatabaseSchemaInfo> GetSchemaAsync(
    DatabaseConnection connection,
    string? database,
    CancellationToken cancellationToken = default,
    bool preferCache = true);
```

- [ ] **Step 2: Update `DatabaseReadService.GetSchemaAsync` signature**

Add `bool preferCache = true` parameter. No logic change yet — just the signature so callers compile. Do the same for `ReadDatabaseListAsync` (add `bool preferCache = true`).

- [ ] **Step 3: Update MCP tool to pass `preferCache: false`**

In `DatabaseGetSchemaTool.cs` line 51, change:
```csharp
var schema = await _readService.GetSchemaAsync(row, database, cancellationToken, preferCache: false);
```

- [ ] **Step 4: Update HTTP endpoint to pass through `?refresh=true`**

In `DatabaseEndpoints.cs` line 303-328, add `bool refresh = false` to the route handler parameters. At line 328, change to:
```csharp
var schema = await readService.GetSchemaAsync(row, database, ct, preferCache: !refresh);
```

- [ ] **Step 5: Verify build compiles**

Run: `dotnet build src/backend/Eling.Backend/Eling.Backend.csproj --no-restore`
Expected: BUILD SUCCEEDED

- [ ] **Step 6: Commit**

```bash
git add src/backend/Eling.Core/Database/IDatabaseReadService.cs src/backend/Eling.Core/Database/DatabaseReadService.cs src/backend/Eling.Backend/Mcp/Tools/DatabaseGetSchemaTool.cs src/backend/Eling.Backend/Endpoints/DatabaseEndpoints.cs
git commit -m "feat(db-cache): add preferCache param to GetSchemaAsync, MCP stays live-first"
```

---

### Task 3: Implement cache-first logic in `DatabaseReadService.GetSchemaAsync`

**Files:**
- Modify: `src/backend/Eling.Core/Database/DatabaseReadService.cs:70-108`
- Test: `tests/Eling.Core.Tests/DatabaseReadServiceTests.cs`

**Interfaces:**
- Consumes: `IDatabaseReadCache.TryGetSchema`, `IDatabaseReadCache.SetSchema`, `CacheSource` from Task 1, `preferCache` from Task 2
- Produces: cache-first behavior used by Tasks 4-5

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task GetSchemaAsync_CacheHit_Fresh_ReturnsLive_NoRefresh()
{
    // Arrange: mock cache returns entry with cachedAt = 10s ago
    // Act: GetSchemaAsync(preferCache: true)
    // Assert: source == Live, fromCache == false, adapter never called
}

[Fact]
public async Task GetSchemaAsync_CacheHit_Stale_ReturnsRefreshing_FiresBackground()
{
    // Arrange: mock cache returns entry with cachedAt = 60s ago
    // Act: GetSchemaAsync(preferCache: true)
    // Assert: source == Refreshing, fromCache == true, cacheExpiresAt and cacheTtlSeconds set
    // Assert: adapter called once in background (wait briefly for Task.Run)
}

[Fact]
public async Task GetSchemaAsync_CacheHit_PreferCacheFalse_GoesLive()
{
    // Arrange: mock cache returns entry
    // Act: GetSchemaAsync(preferCache: false)
    // Assert: adapter called directly, cache ignored
}

[Fact]
public async Task GetSchemaAsync_NoCacheEntry_GoesLive()
{
    // Arrange: mock cache returns false
    // Act: GetSchemaAsync(preferCache: true)
    // Assert: adapter called directly
}

[Fact]
public async Task GetSchemaAsync_BackgroundRefreshFails_SourceBecomesOffline()
{
    // Arrange: stale cache, adapter throws DatabaseUnavailableException in background
    // Act: GetSchemaAsync(preferCache: true)
    // Assert: first response is Refreshing; after background completes, next call returns Offline
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Eling.Core.Tests --filter "GetSchemaAsync_CacheHit" --no-restore`
Expected: FAIL

- [ ] **Step 3: Implement cache-first logic in `GetSchemaAsync`**

Replace the current body (lines 70-108) with this flow:

1. If `preferCache` is true, call `_cache.TryGetSchema(connection.Id, database, out var cached, out var cachedAt)`.
2. If hit and not expired:
   - Compute age = `DateTimeOffset.UtcNow - cachedAt`.
   - If age < 30s: return `cached with { Source = CacheSource.Live, FromCache removed, CacheExpiresAt = cachedAt + DefaultMaxAge, CacheTtlSeconds = remaining seconds }`. No adapter call.
   - If age >= 30s: fire `RefreshSchemaInBackground(connection, database, cancellationToken)`. Return `cached with { Source = CacheSource.Refreshing, CacheExpiresAt = ..., CacheTtlSeconds = ... }`.
3. If miss or `preferCache` false: go to adapter directly (existing code path).
4. On `DatabaseUnavailableException`: try cache. If hit, return with `Source = CacheSource.Offline`. If miss, throw.

Add a `ConcurrentDictionary<string, Task>` field `_refreshing` keyed by `$"{connection.Id}|{database}"` for single-flight. `RefreshSchemaInBackground` checks if key exists; if so, skip. Otherwise `Task.Run` the adapter call, `SetSchema` on success, remove key on completion. Swallow all exceptions inside the `Task.Run` (log warning).

Compute `CacheTtlSeconds` as `(long)(cachedAt.Value + IDatabaseReadCache.DefaultMaxAge - DateTimeOffset.UtcNow).TotalSeconds`, clamped to 0 minimum.

Apply the same pattern to `ReadDatabaseListAsync` (the server-level path).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Eling.Core.Tests --filter "GetSchemaAsync_" --no-restore`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/backend/Eling.Core/Database/DatabaseReadService.cs tests/Eling.Core.Tests/DatabaseReadServiceTests.cs
git commit -m "feat(db-cache): cache-first GetSchemaAsync with background refresh and single-flight"
```

---

### Task 4: Update `JsonDatabaseReadCache` to compute TTL on read

**Files:**
- Modify: `src/backend/Eling.Core/Database/JsonDatabaseReadCache.cs:43-88`
- Test: `tests/Eling.Core.Tests/JsonDatabaseReadCacheTests.cs` (existing)

**Interfaces:**
- Consumes: `IDatabaseReadCache.DefaultMaxAge`
- Produces: no change to public API — `TryGetSchema` still returns `cachedAt`; TTL computed by caller (Task 3)

- [ ] **Step 1: Verify existing cache tests still pass**

Run: `dotnet test tests/Eling.Core.Tests --filter "JsonDatabaseReadCacheTests" --no-restore`
Expected: PASS (no changes needed to cache file itself — TTL is computed by the service layer from `cachedAt`)

- [ ] **Step 2: Commit (no-op if tests pass)**

If no changes needed, skip commit. This task is a verification gate.

---

### Task 5: Update dashboard frontend for new response shape

**Files:**
- Modify: `src/frontend/Eling.Dashboard/src/app/dashboard/databases/Databases.tsx:91-97` (type), `1164-1168` (fetchSchema), `1171-1194` (ensureSchema), `2034-2036` (badge render)

**Interfaces:**
- Consumes: `source`, `cacheExpiresAt`, `cacheTtlSeconds` from backend JSON
- Produces: UI rendering of TTL, force-refresh on manual actions

- [ ] **Step 1: Update TypeScript types**

At line 91-97, add to `BrowseResult` and `SchemaInfo`:
```typescript
source?: "live" | "refreshing" | "offline"
cacheExpiresAt?: string | null
cacheTtlSeconds?: number | null
```

Keep `fromCache` and `cachedAt` — backend still sends them.

- [ ] **Step 2: Update `fetchSchema` to accept `refresh` param**

At line 1164-1168, change to:
```typescript
const fetchSchema = async (id: string, database?: string | null, refresh?: boolean) => {
  const params = new URLSearchParams()
  if (database) params.set("database", database)
  if (refresh) params.set("refresh", "true")
  const qs = params.toString() ? "?" + params.toString() : ""
  const res = await fetch(API + "/" + id + "/schema" + qs)
  if (!res.ok) throw new Error(parseApiError(await res.text()))
  return await res.json()
}
```

- [ ] **Step 3: Update `ensureSchema` to pass `refresh` when forced**

At line 1171-1194, change the `force` path to call `fetchSchema(id, null, true)`.

- [ ] **Step 4: Update badge rendering to show TTL when offline**

At line 2034-2036, when `source === "offline"`, append TTL info:
```tsx
{schemaInfo?.source === "offline" && schemaInfo?.cacheTtlSeconds != null ? (
  <span className="text-xs ml-1">expires in {formatTtl(schemaInfo.cacheTtlSeconds)}</span>
) : null}
```

Add helper:
```typescript
const formatTtl = (seconds: number): string => {
  if (seconds <= 0) return "expired"
  if (seconds < 60) return `${seconds}s`
  const min = Math.floor(seconds / 60)
  if (min < 60) return `${min}m`
  const hr = Math.floor(min / 60)
  return `${hr}h`
}
```

- [ ] **Step 5: Update context menu "Refresh schema" to force**

Find the `loadSchema` call in the context menu (around line 2533). Change to call `fetchSchema(id, null, true)` then update state.

- [ ] **Step 6: Verify TypeScript compiles**

Run: `pnpm --filter eling-dashboard exec tsc --noEmit`
Expected: no errors

- [ ] **Step 7: Commit**

```bash
git add src/frontend/Eling.Dashboard/src/app/dashboard/databases/Databases.tsx
git commit -m "feat(db-cache): dashboard reads source/TTL, sends ?refresh=true on force"
```

---

### Task 6: Integration tests and audit trail

**Files:**
- Modify: `tests/Eling.Backend.Tests/DatabaseReadCacheEndpointTests.cs` (existing)
- Modify: `src/backend/Eling.Backend/Endpoints/DatabaseEndpoints.cs:337-348` (audit)

**Interfaces:**
- Consumes: everything from Tasks 1-5
- Produces: verified end-to-end behavior

- [ ] **Step 1: Write endpoint integration tests**

```csharp
[Fact]
public async Task Schema_CacheHit_ReturnsSourceLive_WhenFresh()
{
    // Seed cache with entry < 30s old
    // GET /api/databases/{id}/schema
    // Assert: 200, source == "live", fromCache == false
}

[Fact]
public async Task Schema_CacheHit_ReturnsSourceRefreshing_WhenStale()
{
    // Seed cache with entry > 30s old
    // GET /api/databases/{id}/schema
    // Assert: 200, source == "refreshing", fromCache == true, cacheExpiresAt present
}

[Fact]
public async Task Schema_RefreshTrue_BypassesCache()
{
    // Seed cache
    // GET /api/databases/{id}/schema?refresh=true
    // Assert: adapter called, source == "live"
}
```

- [ ] **Step 2: Run tests**

Run: `dotnet test tests/Eling.Backend.Tests --filter "Schema_CacheHit|Schema_RefreshTrue" --no-restore`
Expected: PASS

- [ ] **Step 3: Update audit event to include `Source`**

In `DatabaseEndpoints.cs` around line 347, the `AuditEvent` already has `FromCache = schema.FromCache`. Since `FromCache` is now computed from `Source`, this stays correct. No change needed.

- [ ] **Step 4: Run full test suite**

Run: `dotnet test --no-restore`
Expected: all pass

- [ ] **Step 5: Commit**

```bash
git add tests/Eling.Backend.Tests/DatabaseReadCacheEndpointTests.cs
git commit -m "test(db-cache): integration tests for cache-first schema endpoint"
```

</content>