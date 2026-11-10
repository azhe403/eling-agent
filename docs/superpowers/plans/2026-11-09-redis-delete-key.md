# Redis single-key and subtree delete Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the Databases page delete one Redis key or one whole prefix subtree, confirmed by a dialog, without touching the read-only agent surface.

**Architecture:** Three new dashboard-only key routes reuse `RedisKeyRoute.OpenAsync`/`RefuseIndex`/`RunAsync` and extend `IRedisKeyReadService` with single-key delete, a capped prefix preview for the confirmation list, and subtree delete under the existing scan budgets; the dashboard adds a per-row trash button on key rows and prefix rows, an `AlertDialog` per shape, and hook methods that clear the inspector and reload the root page on success.

**Tech Stack:** .NET 10, StackExchange.Redis 3.4.20, xUnit, Next.js 16, React 19, TypeScript, Tailwind, shadcn-style components from `@/components/ui` (base-ui AlertDialog), vitest.

**Spec:** `docs/superpowers/specs/2026-11-09-redis-delete-key-design.md`

## Global Constraints

- `key`, `prefix`, and `database` travel in the query string, never as path segments (same rule as `keys` and `keys/inspect`).
- Database index 0–15 via `RedisKeyRoute.RefuseIndex`; standalone only; `AllowAdmin = false` unchanged.
- Cluster and Sentinel stay rejected with the existing message.
- Tree stays lazy; inspect TTL must not use `PEXPIRETIME`.
- Subtree delete and preview respect 2000 keys scanned, 3000 ms; bigger than budget answers truncated, never silent partial.
- Affected-key list capped at 100 keys with total count and `truncated` flag.
- No `KEYS` command in any path; prefix matching via `KeysAsync` pattern `prefix*` only.
- Dashboard-only: no MCP tool, no `allowAgentMutations`/`requireApproval` gate, no `DEL` in `RedisCommandSafety`.
- One top-level type per file; records chopped one parameter per line; no tuples; JSON camelCase; conventional commits.

## Review Focus

- **A key containing `/`, `:` or `%`.** Expect query-string transport to carry it intact; a path-segment key would split the route and a `%2F` would round-trip differently depending on the proxy. Test: single-key delete with key `a/b:c` against a mocked `IDatabase` proves the exact string reaches `KeyDeleteAsync` (Task 1).
- **Empty prefix (the root) treated as a subtree.** Expect 400, never a flush — deleting `` would be `FLUSHDB`. Test: `DeletePrefixAsync` with `prefix == ""` throws `ArgumentException("prefix must not be blank")` before the pool is touched (Task 1).
- **A prefix bigger than the scan budget (2000 keys / 3000 ms).** Expect `truncated: true` and only a prefix of the keys deleted, with the toast reporting the truth. Test: a pool whose `IAsyncEnumerable` yields > budget proves `Deleted` stops at budget and `Truncated` is true (Tasks 1-2).
- **A key that is already gone.** Expect 404 `key not found`, no audit success, and `RemoveKey` still called so an offline read does not resurrect it. Test: mocked `KeyDeleteAsync` returning false maps to `KeyNotFoundException` (Tasks 1-2).
- **Redis unavailable mid-delete and credentials rejected.** Expect 503 `redis unavailable` for unreachable and 502 `redis credentials rejected` for `NOAUTH`/`WRONGPASS`, both via the existing `RedisKeyRoute.RunAsync` ladder; a cached inspect for the deleted key is never served after success. Test: endpoint tests with a stub that throws `TimeoutException` and a credential exception (Task 2).

---

### Task 1: Core single-key delete, prefix preview, and subtree delete

**Files:**
- Modify: `src/backend/Eling.Core/Database/DatabaseAuditActions.cs` — add `RedisDelete = "redis_delete"` and `RedisPrefixDelete = "redis_prefix_delete"`
- Modify: `src/backend/Eling.Core/Database/IRedisKeyReadService.cs` — add three methods (signature below)
- Create: `src/backend/Eling.Core/Database/RedisKeyDeleteResult.cs`
- Create: `src/backend/Eling.Core/Database/RedisPrefixPreviewResult.cs`
- Create: `src/backend/Eling.Core/Database/RedisPrefixDeleteResult.cs`
- Modify: `src/backend/Eling.Core/Database/RedisKeyReadService.cs` — implement them
- Test: `tests/Eling.Core.Tests/RedisKeyDeleteTests.cs` (new)

**Interfaces:**
- Consumes: `IRedisConnectionPool.GetAsync(DatabaseConnection, CancellationToken) -> Task<IConnectionMultiplexer>`, `IRedisReadCache.RemoveKey(string, RedisDatabaseIndex, string)`, `IDatabase.KeyDeleteAsync(RedisKey, CommandFlags) -> Task<bool>`, `IServer.KeysAsync(int, RedisValue pattern, int pageSize, long cursor, int pageOffset)`.
- Produces:
  - `public sealed record RedisKeyDeleteResult(bool Deleted);` (one parameter per line)
  - `public sealed record RedisPrefixPreviewResult(string Prefix, IReadOnlyList<string> Keys, long Total, bool Truncated);`
  - `public sealed record RedisPrefixDeleteResult(long Deleted, bool Truncated);`
  - `Task<RedisKeyDeleteResult> DeleteKeyAsync(DatabaseConnection connection, RedisDatabaseIndex database, string key, CancellationToken ct = default);` — blank key throws `ArgumentException("key must not be blank")`, missing key throws `KeyNotFoundException("key not found")`, calls `_cache.RemoveKey(connection.Id, database, key)` on success AND on missing, issues `KeyDeleteAsync` on `mux.GetDatabase(database.Value)`.
  - `Task<RedisPrefixPreviewResult> PreviewPrefixAsync(DatabaseConnection connection, RedisDatabaseIndex database, string prefix, CancellationToken ct = default);` — blank prefix throws `ArgumentException("prefix must not be blank")`, enumerates `prefix*` with `pageSize 200`, caps list at 100, counts `Total` up to 2000 budget, `Truncated` when budget stops scan or list capped.
  - `Task<RedisPrefixDeleteResult> DeletePrefixAsync(DatabaseConnection connection, RedisDatabaseIndex database, string prefix, CancellationToken ct = default);` — same blank guard, enumerates `prefix*` with budget 2000/3000ms, deletes each via `KeyDeleteAsync`, calls `RemoveKey` per deleted key, returns count and `Truncated`.

- [ ] **Step 1: Write the failing test** `RedisKeyDeleteTests` with a fake `IRedisConnectionPool` returning a fake `IConnectionMultiplexer`/`IDatabase`/`IServer` (Moq with same overload arity as `RedisKeyReadService` uses). Cases:
  - `DeleteKey_ExistingKey_RemovesAndReturnsTrue` — fake `KeyDeleteAsync` returns true, result `Deleted` true, `RemoveKey` verified once
  - `DeleteKey_KeyWithSlashAndColon_ReachesRedisIntact` — key `a/b:c` is passed verbatim to `KeyDeleteAsync`
  - `DeleteKey_MissingKey_ThrowsKeyNotFound` — fake returns false, throws `KeyNotFoundException`, `RemoveKey` still verified
  - `DeleteKey_BlankKey_RefusedBeforePool` — `ArgumentException`, `GetAsync` call count 0
  - `PreviewPrefix_SmallPrefix_ReturnsKeysTotalTruncatedFalse` — fake `KeysAsync` yields `["user:1:email","user:1:name"]`, total 2, truncated false, keys length 2
  - `PreviewPrefix_LargePrefix_CapsListAt100AndFlagsTruncated` — yields 150 keys, `Keys.Count == 100`, `Total >= 100`, `Truncated` true
  - `DeletePrefix_EmptyPrefix_RefusedBeforePool` — `ArgumentException`, pool untouched
  - `DeletePrefix_BudgetHit_ReportsTruncated` — fake scan yields >2000 keys, `Deleted <= 2000`, `Truncated` true
  Run: `dotnet test tests/Eling.Core.Tests --filter FullyQualifiedName~RedisKeyDeleteTests` → compile fails (types/methods absent).

- [ ] **Step 2: Add audit constants and the three records.** Two constants next to `RedisDatabases`; records with one parameter per line per `CodeOrganizationConventionTests`.

- [ ] **Step 3: Extend `IRedisKeyReadService`** with the three signatures above; update the class doc (`Nothing here writes` line moves).

- [ ] **Step 4: Implement in `RedisKeyReadService`.** Reuse `GetServer`, `EnsureStandalone`, the `Stopwatch`/budget loop already in `ScanAsync` (copy its 2000/3000ms guard, pattern `prefix + "*"`), `IsUnreachable` classification for credential vs unreachable, and `_cache.RemoveKey`.

- [ ] **Step 5: Run tests green.** `dotnet test tests/Eling.Core.Tests --filter FullyQualifiedName~RedisKeyDeleteTests` → pass, then `dotnet test tests/Eling.Core.Tests` → no regression.

- [ ] **Step 6: Commit.** `feat(core): delete redis key and prefix subtree behind budgets`

---

### Task 2: Serve the delete routes and preview

**Files:**
- Create: `src/backend/Eling.Backend/Dtos/RedisKeyDeleteDto.cs`
- Create: `src/backend/Eling.Backend/Dtos/RedisPrefixPreviewDto.cs`
- Create: `src/backend/Eling.Backend/Dtos/RedisPrefixDeleteDto.cs`
- Modify: `src/backend/Eling.Backend/Endpoints/DatabaseKeyEndpoints.cs` — add three routes
- Test: `tests/Eling.Backend.Tests/RedisKeyDeleteEndpointTests.cs` (new)

**Interfaces:**
- Consumes: `IRedisKeyReadService.DeleteKeyAsync` / `PreviewPrefixAsync` / `DeletePrefixAsync` (Task 1), `RedisKeyRoute.OpenAsync` / `RefuseIndex` / `RunAsync` (exists), `IDatabaseConnectionStore.FindAsync`, `IAuditLogger.RecordAsync`.
- Produces:
  - `public sealed record RedisKeyDeleteDto(bool Deleted);` → `{"deleted":true}`
  - `public sealed record RedisPrefixPreviewDto(string Prefix, string[] Keys, long Total, bool Truncated);` → `{"prefix":"user:","keys":[...],"total":2,"truncated":false}`
  - `public sealed record RedisPrefixDeleteDto(long Deleted, bool Truncated);` → `{"deleted":2,"truncated":false}`
  - `DELETE /api/databases/{id}/keys/delete?database=&key=` → 200 DTO; 400 blank key / out-of-range db / non-Redis; 404 unknown id or missing key; 502 credentials; 503 unavailable. Audits `redis_delete`.
  - `GET /api/databases/{id}/keys/preview?database=&prefix=` → 200 DTO; same gate; empty prefix 400. Audits `redis_prefix_preview` — builder adds `DatabaseAuditActions.RedisPrefixPreview = "redis_prefix_preview"` in this task (read-only, same ladder).
  - `DELETE /api/databases/{id}/keys/prefix?database=&prefix=` → 200 DTO; empty prefix 400; audits `redis_prefix_delete` with `ResultCount = deleted`.

- [ ] **Step 1: Write the failing test** using the `WebApplication.CreateSlimBuilder` + `UseTestServer` + `JsonDatabaseConnectionStore` harness from `RedisDatabaseListEndpointTests`, stubbing `IRedisKeyReadService` as a private nested class. Cases:
  - `DeleteKey_UnknownId_NotFound`
  - `DeleteKey_NonRedis_BadRequestContainsKeyBrowsingRequiresRedis`
  - `DeleteKey_OutOfRangeDatabase_BadRequestRefusesIndex`
  - `DeleteKey_BlankKey_BadRequest`
  - `DeleteKey_Redis_AnswersDeletedTrue` (stub returns true → 200 `{"deleted":true}`)
  - `PreviewPrefix_EmptyPrefix_BadRequest`
  - `PreviewPrefix_Redis_AnswersCappedList` (stub returns 100 keys + total 150 + truncated true → 200)
  - `DeletePrefix_EmptyPrefix_BadRequest`
  - `DeletePrefix_Redis_AnswersCount` (stub returns `Deleted=2, Truncated=false`)
  - `DeleteKey_MissingKey_NotFoundBody` (stub throws `KeyNotFoundException` → 404 with `{"error":"key not found"}`)
  - `DeleteKey_Unreachable_AnswersUnavailable` (stub throws `TimeoutException` → 503 `redis unavailable`)
  - `DeleteKey_CredentialsRejected_AnswersCredentialsRejected` (stub throws `RedisConnectionException(AuthenticationFailure, "WRONGPASS ...")` → 502 `redis credentials rejected`)
  Run: `dotnet test tests/Eling.Backend.Tests --filter FullyQualifiedName~RedisKeyDeleteEndpointTests` → 404s/compile errors.

- [ ] **Step 2: Add the three DTOs and the preview audit constant.**

- [ ] **Step 3: Add the routes** modelled on `GET /{id}/keys/inspect`: gate → blank check → `RefuseIndex` → `RunAsync` with matching action/`query` (`key` for single, `prefix` for preview/prefix-delete). Preview uses `action = RedisPrefixPreview`; delete/prefix-delete use their constants; `ResultCount` set where the core result carries a count.

- [ ] **Step 4: Run tests green.** `dotnet test tests/Eling.Backend.Tests --filter FullyQualifiedName~RedisKeyDeleteEndpointTests` → pass.

- [ ] **Step 5: Run the whole backend suite.** `dotnet test tests/Eling.Backend.Tests` → green, including `CodeOrganizationConventionTests`.

- [ ] **Step 6: Commit.** `feat(backend): serve redis key and subtree delete`

---

### Task 3: Trash button on each key row and each prefix row

**Files:**
- Modify: `src/frontend/Eling.Dashboard/src/app/dashboard/databases/redis-tree.tsx` — `RedisKeyRow`, `RedisNodeRow`, `RedisPrefixTree`
- Test: `src/frontend/Eling.Dashboard/src/app/dashboard/databases/redis-tree.test.tsx` (new, vitest + `@testing-library/react` if already present; else pure prop test via `render` from `vitest` DOM)

**Interfaces:**
- Consumes: `RedisKeyRow(item: {key, type, ttlSeconds}, active, onSelect)` and `RedisNodeRow(item: {segment, prefix}, expanded, onToggle)` (existing).
- Produces:
  - `RedisKeyRow` gains `onDelete: (key: string) => void`; renders a container `div` (flex) with the existing select `Button` left and a right `Button` (`variant="ghost" size="icon"`, `aria-label="Delete key {key}"`, `Trash2` icon) whose `onClick` does `e.stopPropagation(); onDelete(item.key)`.
  - `RedisNodeRow` gains `onDelete: (prefix: string) => void` with the same right button (`aria-label="Delete prefix {prefix}"`), hiding it when `prefix === ""` (root never deletes).
  - `RedisPrefixTree` gains `onDeleteKey: (key: string) => void` and `onDeletePrefix: (prefix: string) => void` and threads them to rows.

- [ ] **Step 1: Write the failing test.** Render one key row → click trash → `onDelete` called with the key and `onSelect` NOT called. Render one node row → click trash → `onDelete` called with prefix and `onToggle` NOT called. Node row with `prefix === ""` renders no trash.

- [ ] **Step 2: Run to verify fail.** `pnpm --prefix src/frontend/Eling.Dashboard exec vitest run src/app/dashboard/databases/redis-tree.test.tsx` → fail (prop missing).

- [ ] **Step 3: Implement the rows.** Keep the existing select/expand `Button` markup; wrap in `div` with `flex w-full items-center`, trash button keeps `size="sm"` visual parity with `Close key inspector` button. `title={item.key}` stays on the container.

- [ ] **Step 4: Run tests green + typecheck.** vitest → pass; `pnpm --prefix src/frontend/Eling.Dashboard exec tsc --noEmit` → clean.

- [ ] **Step 5: Commit.** `feat(dashboard): trash button on redis key and prefix rows`

---

### Task 4: The two confirmation dialogs

**Files:**
- Create: `src/frontend/Eling.Dashboard/src/app/dashboard/databases/components/dialogs/RedisDeleteConfirmDialog.tsx`
- Create: `src/frontend/Eling.Dashboard/src/app/dashboard/databases/components/dialogs/RedisPrefixDeleteConfirmDialog.tsx`
- Test: `src/frontend/Eling.Dashboard/src/app/dashboard/databases/components/dialogs/RedisDeleteConfirmDialog.test.tsx` (new)

**Interfaces:**
- Consumes: `AlertDialog, AlertDialogContent/Header/Title/Description/Footer/Action/Cancel` from `@/components/ui/alert-dialog` (same primitives as `DeleteRowConfirmDialog`).
- Produces:
  - `RedisDeleteConfirmDialogProps { open: boolean; keyName: string; mutating: boolean; onConfirm: () => void; onCancel: () => void }` — title `Delete key?`, description names `<span className="font-mono">{keyName}</span>` “This cannot be undone.”, footer `Cancel` / destructive `Delete` (`Deleting...` when `mutating`).
  - `RedisPrefixDeleteConfirmDialogProps { open: boolean; prefix: string; total: number; keys: string[]; truncated: boolean; loading: boolean; mutating: boolean; onConfirm: () => void; onCancel: () => void }` — title `Delete prefix?`, mono prefix, line `{total} keys under {prefix}` spelled out (`3 keys under user:`), `max-h-60 overflow-y-auto` list of `keys` in mono truncated rows (like `ImportConfirmDialog`), `truncated` → amber note `Showing 100 of {total} — more keys will also be deleted.`, `loading` → `Loading affected keys…` skeleton text, footer same.

- [ ] **Step 1: Write the failing test.** Single dialog renders key name and calls `onConfirm` without closing automatically (`preventDefault` like `DeleteRowConfirmDialog`). Prefix dialog renders total, lists keys, shows truncated note when `truncated` true, and renders loading text when `loading` true.

- [ ] **Step 2: Run to verify fail.** vitest → fail (modules absent).

- [ ] **Step 3: Implement the dialogs** by copying `DeleteRowConfirmDialog` structure; prefix list uses `div className="max-h-60 space-y-1 overflow-y-auto"` and per-key `rounded border bg-muted/40 px-2 py-1 text-sm font-mono break-all` rows.

- [ ] **Step 4: Run tests green + tsc.** vitest → pass; `tsc --noEmit` clean.

- [ ] **Step 5: Commit.** `feat(dashboard): redis delete confirmation dialogs`

---

### Task 5: Hook and wiring — delete single, preview, and subtree

**Files:**
- Modify: `src/frontend/Eling.Dashboard/src/app/dashboard/databases/hooks/useRedisBrowser.ts` — add `redisDeleteTarget`, `redisPreview`, `deleteRedisKey`, `previewRedisPrefix`, `deleteRedisPrefix`, dialog open setters
- Modify: `src/frontend/Eling.Dashboard/src/app/dashboard/databases/redis-browser.ts` — add `fetchRedisKeyDelete`, `fetchRedisPrefixPreview`, `fetchRedisPrefixDelete` (mirror `fetchRedisDatabases`)
- Modify: `src/frontend/Eling.Dashboard/src/app/dashboard/databases/components/redis-browser/RedisBrowser.tsx` — accept `onDeleteKey`, `onDeletePrefix`, dialog state props and render both dialogs
- Modify: `src/frontend/Eling.Dashboard/src/app/dashboard/databases/Databases.tsx` — pass through (construction of `useRedisBrowser` + `RedisBrowser` props)
- Test: `src/frontend/Eling.Dashboard/src/app/dashboard/databases/redis-browser.test.ts` — extend with delete/preview fetch stubs

**Interfaces:**
- Consumes: Task 2 routes (`DELETE .../keys/delete`, `GET .../keys/preview`, `DELETE .../keys/prefix`), `API, parseApiError` from `utils/connection-helpers`, `notifyDb`.
- Produces:
  - `fetchRedisKeyDelete = async (connId, database, key): Promise<{deleted:true}>` — `DELETE` with `URLSearchParams(database,key)`, throws parsed error on non-OK.
  - `fetchRedisPrefixPreview = async (connId, database, prefix): Promise<{prefix, keys, total, truncated}>` — `GET` with `database,prefix`.
  - `fetchRedisPrefixDelete = async (connId, database, prefix): Promise<{deleted, truncated}>` — `DELETE` with `database,prefix`.
  - Hook: `deleteRedisKey(connId, db, key) → Promise<void>` — calls fetch, on success clears `redisInspect/redisInspectKey` when equal, drops entries covering that key from `redisPages`, reloads root via `fetchRedisPrefix(connId, db, redisRootPrefix)` and `loadRedisDatabases(connId)`, closes dialog; on failure `notifyDb("error","Redis delete failed",msg)` and keeps dialog open. `previewRedisPrefix` sets `redisPreview` + `loading` flag used by the prefix dialog. `deleteRedisPrefix` same pattern using preview total for the toast.

- [ ] **Step 1: Write the failing test.** Stub `globalThis.fetch` with `vi.stubGlobal`: each fetch rejects on non-OK and resolves parsed body on 200; correct `database` and `key`/`prefix` in the URL. Hook-level test: successful delete clears `redisInspectKey` when it matches.

- [ ] **Step 2: Run to verify fail.** `pnpm --prefix src/frontend/Eling.Dashboard test` → fail.

- [ ] **Step 3: Implement fetches + hook methods + wiring.** Dialog open state lives in the hook (`redisDeleteOpen`, `redisPrefixPreviewOpen` + target), `RedisBrowser` renders dialogs and passes `onDeleteKey/onDeletePrefix` into `RedisPrefixTree`; `Databases.tsx` only forwards. Keep `"use client"` boundary; follow one-file-one-hook convention.

- [ ] **Step 4: Run tests green + lint.** `pnpm --prefix src/frontend/Eling.Dashboard test` green; `pnpm --prefix src/frontend/Eling.Dashboard exec eslint src/app/dashboard/databases` no new errors; `tsc --noEmit` clean.

- [ ] **Step 5: Commit.** `feat(dashboard): wire redis delete actions`

---

### Task 6: Full verification

**Files:** none

- [ ] **Step 1: Build and test the backend.** `dotnet build`; `dotnet test tests/Eling.Backend.Tests`; `dotnet test tests/Eling.Core.Tests` → green, including `CodeOrganizationConventionTests`. Use `$env:ELING_OUTPUT_ROOT=".bin-verify"` so the build does not fight the dev server over `.bin`.

- [ ] **Step 2: Check the frontend.** `pnpm --prefix src/frontend/Eling.Dashboard exec tsc --noEmit` → clean; `pnpm --prefix src/frontend/Eling.Dashboard test` → green; `pnpm --prefix src/frontend/Eling.Dashboard exec eslint src/app/dashboard/databases` → no errors.

- [ ] **Step 3: Check it in the browser** against the dev backend on 4417 and the dashboard on 4427, with the Redis connection `dev-ibl-redis`:
  - right-side trash on a key row opens `Delete key?` without selecting; Cancel leaves everything; Yes removes just that key, inspector closes if it showed it, sidebar count drops by one
  - right-side trash on a `📁` row opens `Delete prefix?` with the total number and the capped scrollable list; Cancel leaves everything; Yes removes just that subtree, the list matches what disappeared, sidebar count drops to match
  - a `prefix` with >100 keys shows the truncated note

- [ ] **Step 4: Commit.** none — verification only.
