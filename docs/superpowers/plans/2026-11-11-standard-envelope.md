# Standard Response Envelope — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **Status (2026-11-12): Tasks 1-6 done and committed; Task 7 (live verification) dropped — live smoke skipped by user decision, TestServer reproduces the full pipeline (see the spec's Implemented note). Commits so far: 66f55dd (middleware + tests), e07fb5a (domain codes), 45e39e5 (apiFetch), 6e51efb (databases explorer), ba232b9 (rest of dashboard), 7c79e17 (Desktop client), plus the cache-first batch (887d0eb, 28fc64b, 34505a3, 3ad5695, 0f8ff6d) and the two-axis review fixes.

**Goal:** Every JSON response from the Eling backend carries the standard envelope `{ code, message, data }` — machine `code` first (always filled), always-filled English `message`, `data` last — and every HTTP client in the repo (dashboard frontend, `Eling.Desktop`) reads it through one unwrap helper.

**Architecture:** One boundary `ApiEnvelopeMiddleware`, registered FIRST in `DashboardRoutes.Map(app)`, decides pass-through at first write, transforms after the pipeline completes. No endpoint call sites are rewritten for the shape; only a few polish spots get domain codes/messages. Clients get one helper each (`apiFetch<T>`, `ReadEnvelopeAsync<T>`) and all call sites migrate mechanically.

**Tech Stack:** ASP.NET Core minimal APIs + MVC, xUnit + `TestServer` (backend), Next.js + vitest (dashboard), .NET MAUI/WPF `Eling.Desktop` with `HttpClient` (desktop).

**Spec:** `docs/superpowers/specs/2026-11-11-standard-envelope-design.md`

## Global Constraints

Copied verbatim from the spec; every task's requirements implicitly include this section.

- No JSON response from the backend lacks the envelope. No frontend or desktop call site reads a raw response outside `apiFetch`/`ReadEnvelopeAsync<T>`.
- Error responses are enveloped **regardless of content type**; success-path non-JSON bodies (SSE, static files/SPA, `text/plain`) pass through raw; `MapFallbackToFile` for unmatched `/api/*` stays untouched.
- Key order is exactly `code`, `message`, `data`. `code` is always filled: `ok` on success, a `lower_snake_case` identity on ≥400. `message` is always filled (verb-derived default on success: GET/HEAD → "The resource was read successfully from the server.", POST/PUT/PATCH → "The request was processed successfully by the server.", DELETE → "The resource was deleted successfully by the server.", anything else → "The request was completed successfully by the server." — or the endpoint's `HttpContext.Items["successMessage"]`). Failure messages are full English prose (cause + next step), ≥6 words, reusing an endpoint's own human sentence when present.
- `204`, `304` and HEAD requests never carry a body, even on errors. Never a zero-byte response anywhere else.
- Generic codes derived from status (`not_found`, `bad_request`, `forbidden`, `conflict`, `unsupported_media_type`, `internal_error`, ...); domain codes only from the polished endpoints (`connection_not_found`, `path_outside_root`, `binary_file`).
- `/health` is enveloped like everything else. `apiFetch` throws `ApiError(status, code, message)` on ≥400; throws `ApiError("invalid_response")` on a 2xx that isn't the envelope; returns `undefined` on 204/empty.
- Commits are split per logical context and hooks are mandatory — this repo blocks `--no-verify` and friends. Conventional Commits style as in `git log`.
- Verify with the LITE test filter only (`~Database|~Redis|~Envelope|~ManualConnection` on backend, vitest for dashboard) — never the full suite.

## Review Focus

Five input classes the spec implies but does not pin; most likely first. Each carries the test that owns it.

1. **SSE must not be held hostage by buffering.** A naive swap of `Response.Body` for a `MemoryStream` never releases `data: connected` because `FlushAsync` is a no-op on it. Pin: `ApiEnvelopeTests.Sse_Route_EmitsFramesUnbuffered` (Task 1) — asserts the stream starts and yields `data: connected` when requested, then the caller aborts.
2. **Empty/HEAD/204 responses must stay empty.** Pin: `ApiEnvelopeTests.Head_Route_ReturnsZeroBytes_And_204_StaysEmpty` (Task 1).
3. **Unmatched `/api/*` still serves the SPA.** The stale-backend case then produces SPA-HTML-200, not an error page; `home/data.ts` must detect it through `ApiError` instead of string sniffing. Pin: `lib/api.test.ts` "HTML body → invalid_response" plus the `isMissingRoute` rewrite (Tasks 3 and 5).
4. **`text/plain` error bodies must not vanish.** `LogsEndpoints` writes raw strings with no content type; the envelope pulls them into `message` for ≥400 regardless of content type. Pin: `ApiEnvelopeTests.TextPlainErrorBody_LandsInMessage` (Task 1) and a database test on `DatabaseManualConnectionTests`-style host (Task 2).
5. **An array-typed success must survive the transform.** `data` may be an array, a wrapped `{columns,rows}` result, or `null`; the envelope never nests the payload differently. Pin: `ApiEnvelopeTests.ArrayPayload_RoundTripsAsData` (Task 1).

---

## Task 1: Backend envelope middleware + test suite

**Files:**
- Create: `src/backend/Eling.Backend/Bootstrap/ApiEnvelopeMiddleware.cs`
- Modify: `src/backend/Eling.Backend/Bootstrap/DashboardRoutes.cs:19-52` (register first inside `Map`)
- Test: `tests/Eling.Backend.Tests/ApiEnvelopeTests.cs`

**Interfaces:**
- Produces: `Eling.Backend.Bootstrap.ApiEnvelopeMiddleware : IMiddleware`-style class with `Task InvokeAsync(HttpContext context, RequestDelegate next)`. Consts: `HttpContext.Items["errorCode"]` (string, opt-in domain code), `Items["successMessage"]` (string, opt-in success sentence). Internal verb→default-message map. Transform is applied in one pass after `next()` completes; pass-through decided at first write by a wrapper stream.
- Produces for Task 3+: nothing (clients never see this code) — but the wire shapes below are what client tests assert.

- [ ] **Step 1: Write the failing tests** (`tests/Eling.Backend.Tests/ApiEnvelopeTests.cs`, TestServer, mirror of `DatabaseManualConnectionTests`):

```csharp
[Fact] Get_ObjectPayload_IsEnveloped_WithCodeOk()          // GET a stub route returning new { ok = true } → 200, { code: "ok", message: "The resource was read successfully from the server.", data: { ok: true } }
[Fact] Get_ArrayPayload_RoundTripsAsData()                  // route returns new[] { "a", "b" } → data is the array, nothing else nested
[Fact] BareNotFound_Answers404_WithGenericCode()            // route returns Results.NotFound() → code "not_found", message non-empty, data null
[Fact] EndpointSettingErrorCode_TravelsInRootCode()         // route sets HttpContext.Items["errorCode"]="connection_not_found", returns 404 → code "connection_not_found"
[Fact] EndpointThatThrows_Answers500_InternalError()        // route throws new InvalidOperationException("boom") → code "internal_error", message contains "boom", data null
[Fact] TextPlainErrorBody_LandsInMessage()                  // route writes "Unknown stream 'foo'." as text/plain → code "bad_request", message == raw string
[Fact] EmptyTwoHundred_IsOkAndVerbDerived()                 // route returns Results.Ok() on POST → code "ok", message == "The request was processed successfully by the server."
[Fact] Delete_On204_NeverHasBody()                          // route returns Results.NoContent() for DELETE → 204, zero-length body
[Fact] Sse_Route_EmitsFramesUnbuffered()                    // GET /api/events-like route → response starts, first frame starts with "data: "
[Fact] Head_Route_ReturnsZeroBytes()                        // HEAD on a GET route → zero-length body
```

- [ ] **Step 2: Run the tests and watch them fail**

Run: `dotnet test tests/Eling.Backend.Tests --filter "FullyQualifiedName~ApiEnvelopeTests"`
Expected: FAIL (middleware does not exist / routes not registered).

- [ ] **Step 3: Implement `ApiEnvelopeMiddleware`** in `src/backend/Eling.Backend/Bootstrap/ApiEnvelopeMiddleware.cs`:

`InvokeAsync` swaps `context.Response.Body` for a `PassthroughOrBufferStream` that holds the original stream. On first `Write`/`WriteAsync`:
- if the method is `HEAD`, or the request will produce 204/304 anyway → remember pass-through style copying (headers only), never transform;
- if `ContentType` starts with `text/event-stream` → flip to pass-through (`CopyToAsync` the buffered bytes to the original, set a flag, all subsequent writes go straight to the original with flushes);
- else buffer in a `MemoryStream`.

After `next()`, if not passed-through:
- HEAD/204/304 → copy buffer to original, set `Content-Length`, return.
- else if the response status ≥ 400 → build `{ code, message, data: null }`: message from the endpoint's body if it parses as a JSON string/object (`message`/`detail`/`error`) or raw text, else the status phrase; code = `HttpContext.Items["errorCode"]` if set, else generic by status; reset `ContentType` to `application/json; charset=utf-8`, recompute `Content-Length`.
- else if ContentType is JSON → `{ code: "ok", message: items["successMessage"] or verb-derived default, data: <original payload> }`.
- malformed JSON on a JSON-typed response → `500 internal_error` envelope + `logger.LogWarning`.
Verb defaults table exactly as in the spec. 500 from an unhandled exception → `{ code: "internal_error", message: ex.Message, data: null }` with 500 and a warning logged.

- [ ] **Step 4: Register it in `DashboardRoutes.Map`** as the first `app.Use(...)` statement (before `UseDefaultFiles`), then run the tests

Run: `dotnet test tests/Eling.Backend.Tests --filter "FullyQualifiedName~ApiEnvelopeTests"`
Expected: all 10 PASS.

- [ ] **Step 5: Regression check + commit**

Run: `dotnet test tests/Eling.Backend.Tests --filter "FullyQualifiedName~Database|FullyQualifiedName~Redis|FullyQualifiedName~ManualConnection|FullyQualifiedName~ApiEnvelope"`
Expected: PASS (the earlier `ConnectionNotFound` 404 change now rides the envelope — the assertions that check status codes still hold).

```bash
git add src/backend/Eling.Backend/Bootstrap/ApiEnvelopeMiddleware.cs src/backend/Eling.Backend/Bootstrap/DashboardRoutes.cs tests/Eling.Backend.Tests/ApiEnvelopeTests.cs
git commit -m "feat(backend): add ApiEnvelopeMiddleware for the standard envelope"
```

## Task 2: Domain codes and messages at the three polished endpoint files

**Files:**
- Modify: `src/backend/Eling.Backend/Endpoints/DatabaseEndpoints.cs` — `ConnectionNotFound(id)` sets `HttpContext.Items["errorCode"] = "connection_not_found"` (takes an extra `HttpContext httpContext` param; update its call sites).
- Modify: `src/backend/Eling.Backend/Endpoints/RedisKeyRoute.cs` — `Result.NotFound(id)` becomes `Result.NotFound(id, HttpContext)`-equivalent: `OpenAsync` takes `HttpContext httpContext`, sets the item, message upgraded to prose ("Connection '<id>' is gone — it may have been deleted in another tab or by another eling app; the connection list is being refreshed."). Update its callers (`OpenAsync` and every `Result.NotFound()` caller — grep `RedisKeyRoute.OpenAsync` across `Endpoints/`).
- Modify: `src/backend/Eling.Backend/Endpoints/AgentWorkspaceEndpoints.cs` — the three 403 path-escape cases return prose message + set `errorCode = "path_outside_root"`; the 415 binary case sets `"binary_file"` with a full-sentence message. Minimal-API handlers that need `HttpContext` add the parameter.
- Test: extend `ApiEnvelopeTests.cs` or new `DomainCodeEndpointTests.cs` — TestServer, assert `/api/databases/does-not-exist/schema` → 404 with `code: "connection_not_found"`; `/api/agent/files/list?root=C:\` → 403 `code: "path_outside_root"`; `/api/agent/files/read?root=...&path=<binary>` → 415 `code: "binary_file"`.

- [ ] **Step 1: Write the failing tests** — assertions above, exact code values.

- [ ] **Step 2: Run them; expect FAIL** (codes are generic today).

Run: `dotnet test tests/Eling.Backend.Tests --filter "FullyQualifiedName~DomainCode|FullyQualifiedName~ApiEnvelope"`

- [ ] **Step 3: Implement the three polish edits** as listed. Keep the existing `{ error: "..." }` body of `ConnectionNotFound` — the middleware bridges its `error` string into `message` if it survives, otherwise the prose lives directly in the message; verify once via curl, not by reading.

- [ ] **Step 4: Run the tests; expect PASS** — and the lite suite:

Run: `dotnet test tests/Eling.Backend.Tests --filter "FullyQualifiedName~Database|FullyQualifiedName~Redis|FullyQualifiedName~ManualConnection|FullyQualifiedName~ApiEnvelope|FullyQualifiedName~DomainCode"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/backend/Eling.Backend/Endpoints/DatabaseEndpoints.cs src/backend/Eling.Backend/Endpoints/RedisKeyRoute.cs src/backend/Eling.Backend/Endpoints/AgentWorkspaceEndpoints.cs tests/Eling.Backend.Tests/
git commit -m "feat(backend): domain envelope codes and prose messages at the polished endpoints"
```

## Task 3: Dashboard `apiFetch` helper + unit tests

**Files:**
- Create: `src/frontend/Eling.Dashboard/src/lib/api.ts`
- Test: `src/frontend/Eling.Dashboard/src/lib/api.test.ts`

**Interfaces:**
- Produces: `apiFetch<T>(path: string, init?: RequestInit): Promise<T>` and `ApiError extends Error` with readonly `status: number`, `code: string`. Throws on ≥400 (`code` from root `body.code ?? genericCode(status)`); throws `ApiError(status, "invalid_response", "…")` on a 2xx body that isn't the envelope; returns `body?.data` on 2xx; returns `undefined` on 204/empty body (no JSON parse attempted).
- `genericCode(status)`: 400→bad_request, 401→unauthorized, 403→forbidden, 404→not_found, 405→method_not_allowed, 409→conflict, 413→payload_too_large, 414→uri_too_long, 429→too_many_requests, 5xx→internal_error, else `"http_error"`.

- [ ] **Step 1: Write the failing unit tests** (`vitest`):

```ts
it("unwraps data on 200")            // stub fetch → 200 { code:"ok", message:"...", data:{a:1} } → returns {a:1}
it("throws ApiError with status/code/message on 404") // 404 { code:"connection_not_found", message:"…", data:null } → err.status 404, err.code, err.message
it("throws invalid_response on non-envelope JSON")     // 200 body "hello" or array-missing-envelope → err.code === "invalid_response"
it("returns undefined on 204")                          // 204 with empty body → resolves undefined, no throw
it("throws invalid_response on HTML SPA fallback")      // 200 body "<!doctype html>…" → err.code === "invalid_response"
it("does not throw on plain ok array payload")          // 200 data: array → returns it
```

- [ ] **Step 2: Run them; expect FAIL.**

Run: `pnpm --filter eling-dashboard exec vitest run src/lib/api.test.ts`

- [ ] **Step 3: Implement `src/lib/api.ts`** — sketch is in the spec (`ApiError`, `apiFetch`, `genericCode` table). Export both `ApiError` and `apiFetch` by name.

- [ ] **Step 4: Run them; expect PASS.**

- [ ] **Step 5: Commit**

```bash
git add src/frontend/Eling.Dashboard/src/lib/api.ts src/frontend/Eling.Dashboard/src/lib/api.test.ts
git commit -m "feat(dashboard): apiFetch envelope helper"
```

## Task 4: Migrate the databases explorer — `apiFetch` sweep, group A

**Files:**
- Modify: `src/frontend/Eling.Dashboard/src/app/dashboard/databases/Databases.tsx` (13 `fetch(` sites: list load, connections CRUD, test, import, schema refresh paths, delete)
- Modify: `src/frontend/Eling.Dashboard/src/app/dashboard/databases/hooks/useSchema.ts` (`fetchSchema` uses `apiFetch`; the local `SchemaFetchError` class is deleted; `onConnectionMissing` keeps working on `ApiError.status === 404`)
- Modify: `src/frontend/Eling.Dashboard/src/app/dashboard/databases/redis-browser.ts` (6), `hooks/useRedisBrowser.ts` (4), `hooks/useTableData.ts` (4)
- Verification: `pnpm --filter eling-dashboard exec vitest run src/app/dashboard/databases && pnpm --filter eling-dashboard exec tsc --noEmit`

Migration pattern (apply to every site, no creative retyping):

```ts
// before
const res = await fetch(API + path, { ...init })
if (!res.ok) throw new Error(parseApiError(await res.text()))
const x = (await res.json()) as T
// after
const x = await apiFetch<T>(API + path, init)
```
Error branches that only threw on `res.ok` vanish; `res.status === NNN` checks become `err instanceof ApiError && err.status === NNN`; `parseApiError` calls disappear (it's deleted in Task 5 once nothing references it).

- [ ] **Step 1: Migrate files one by one**, running `tsc --noEmit` after each to keep errors proportional to the last file changed.

- [ ] **Step 2: Fix `useSchema.ts` specifically** — `fetchSchema` returns `apiFetch(...)`; delete the local `SchemaFetchError`; the three catch blocks now check `err instanceof ApiError && err.status === 404` before calling `onConnectionMissing`.

- [ ] **Step 3: Run the databases vitest suite.**

Run: `pnpm --filter eling-dashboard exec vitest run src/app/dashboard/databases`
Expected: existing suites pass; `useSchema`-adjacent tests (if any) updated to `ApiError` or removed (none assert the retired class — check with `grep -n "SchemaFetchError"`).

- [ ] **Step 4: Commit**

```bash
git add src/frontend/Eling.Dashboard/src/app/dashboard/databases
git commit -m "refactor(dashboard): databases explorer reads the envelope via apiFetch"
```

## Task 5: Migrate the rest, delete `parseApiError`, fix `isMissingRoute`

**Files:**
- Modify (all `fetch(` sites, same mechanical pattern): `memories/MemoriesList.tsx` (7), `judge/Judge.tsx` (6), `workspaces/WorkspacesList.tsx` (4), `codebase/CodebaseList.tsx` (3), `codebase/use-codebase-rebuild.ts` (3), `tools/Tools.tsx` (3), `components/workspaces/runtime-rows.ts` (3), `shell/ShellConfigCard.tsx` (3), `logs/Logs.tsx` (3), `components/host-path-picker.tsx` (2), `create/CreateMemory.tsx` (2), `settings/Settings.tsx` (2), `audit-trail/AuditTrailList.tsx` (1), `codebase/use-codebase-search.ts` (1), `codebase/fileDetailApi.ts` (1), `home/data.ts` (1), `hooks/use-host-identity.ts` (1)
- Modify: `audit-trail/AuditTrailList.tsx` — `Promise.all` of page fetches keeps its per-page error handling, toasts read `err.message`.
- Modify: `codebase/fileDetailApi.ts` — `errorMessage()` replaced by reading `ApiError.message`.
- Modify: `home/data.ts` + `home/platform.tsx` — `isMissingRoute` now recognizes `ApiError` (`err.code === "invalid_response" || err.status === 404`) instead of parsing text; `getJson` uses `apiFetch` and treats `invalid_response` as "backend predates this dashboard" exactly as before.
- Delete: `parseApiError` from `src/app/dashboard/databases/utils/connection-helpers.ts` (and its `export` in `utils/index.ts`); the special-cases (`mutations not allowed...`) die with it. Any remaining caller fails loudly at `tsc` — fix by migrating that caller to `apiFetch`.
- Verification: `pnpm --filter eling-dashboard exec tsc --noEmit` + `pnpm --filter eling-dashboard exec vitest run src`

- [ ] **Step 1: Migrate the small pages** (logs, settings, create, audit-trail, host-path-picker, use-host-identity) — show each compiles.

- [ ] **Step 2: Migrate the mid-size pages** (memories, judge, workspaces, tools, shell, codebase trio, fileDetailApi).

- [ ] **Step 3: Rewrite `home/data.ts` and `home/platform.tsx` missing-route detection** per the builder-check note; `getJson` must call `apiFetch` and map `invalid_response` to the stale-backend UX.

- [ ] **Step 4: Delete `parseApiError`, run `tsc --noEmit`; fix every dangling reference.**

- [ ] **Step 5: Full dashboard verification**

Run: `pnpm --filter eling-dashboard exec vitest run src`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/frontend/Eling.Dashboard/src
git commit -m "refactor(dashboard): migrate remaining fetch sites to apiFetch; drop parseApiError"
```

## Task 6: Desktop client migration (`ReadEnvelopeAsync<T>`)

**Files:**
- Modify: `src/desktop/Eling.Desktop/Services/ElingApiClient.cs` — add `ReadEnvelopeAsync<T>(HttpResponseMessage response, string pathForLog)` (internal, mirrors `apiFetch` semantics: on 2xx unwrap `data`; on ≥400 log warning with `code`+`message` from the root and return default; on missing/non-envelope JSON log warning and return default — preserving today's "errors become empty/default DTOs" behavior the resilience tests depend on) and route all 21 `ReadFromJsonAsync<…>`/`GetFromJsonAsync<…>` call sites through it (`grep -n "FromJsonAsync" ElingApiClient.cs` to mark them).
- Test: `tests/Eling.Desktop.Tests/AgentApiTests.cs`, `ChatLoadResilienceTests.cs`, `WorkspaceViewModelTests.cs` — wrap every stub `StringContent("""{...}""")` payload in `{ "code": "ok", "message": "…", "data": … }`.
- Verification: `dotnet test tests/Eling.Desktop.Tests --filter "FullyQualifiedName~AgentApi|FullyQualifiedName~ChatLoadResilience|FullyQualifiedName~WorkspaceViewModel"`

- [ ] **Step 1: Add the helper** next to the other internal helpers in the same file; keep the exact same error semantics (defaults + warnings) so `ChatLoadResilienceTests` semantics survive the envelope.

- [ ] **Step 2: Migrate the 21 call sites** — mechanical; each becomes `ReadEnvelopeAsync<T>(response, "/path")`.

- [ ] **Step 3: Update the stub payloads** in the three test files to the envelope, then run:

Run: `dotnet test tests/Eling.Desktop.Tests`
Expected: PASS (watching especially `GetProvider_ParsesHasKey`, `LoadAsync_RetriesWhileBackendIsUnreachable_ThenFillsTheSidebar`, `WorkspaceViewModel` reads).

- [ ] **Step 4: Commit**

```bash
git add src/desktop/Eling.Desktop/Services/ElingApiClient.cs tests/Eling.Desktop.Tests
git commit -m "feat(desktop): unwrap the standard envelope in ElingApiClient"
```

## Task 7: Live verification against the running dev backend

- [ ] **Step 1: Make sure the dev backend on 4417 is the owner** (`AGENTS.md`: `dotnet watch` reloads changed files by itself — no manual restart during this task) — if it isn't running, start it the usual way.
- [ ] **Step 2: Curl the contract directly:**

```bash
curl -s http://127.0.0.1:4417/api/health
curl -s http://127.0.0.1:4417/api/databases/known-id/schema | head -c 200
curl -s http://127.0.0.1:4417/api/databases/definitely-not-an-id/schema
curl -s -I -X HEAD http://127.0.0.1:4417/api/health        # expect zero bytes
curl -s -N --max-time 3 http://127.0.0.1:4417/api/events/memories   # expect "data: connected" quickly, not a hang
curl -si -X POST http://127.0.0.1:4417/api/coordinator/heartbeat/999999   # expect {"code":"not_found",…} not empty
```

Expected: 200 responses carry `{code:"ok", message:<verb sentence>, data:{…}}`; the 404 carries `connection_not_found` (or `not_found` for the coordinator route — not `connection_not_found` there); HEAD is body-free; SSE streams.

- [ ] **Step 3: lite suite once more** (backend): `dotnet test tests/Eling.Backend.Tests --filter "FullyQualifiedName~Database|FullyQualifiedName~Redis|FullyQualifiedName~ManualConnection|FullyQualifiedName~ApiEnvelope|FullyQualifiedName~DomainCode"`
Expected: PASS.

- [ ] **Step 4: Dashboard sanity**: `pnpm --filter eling-dashboard dev` on 4427 is untouched; refresh the explorer tab and confirm no 404 storm in the network tab (the pruning fix from earlier keeps working), and `/api/health` shows the envelope.

- [ ] **Step 5: Commit any final polish, then update the spec status line** from `Proposed` to `Implemented (2026-11-11)` and commit:

```bash
git add docs/superpowers/specs/2026-11-11-standard-envelope-design.md
git commit -m "docs: envelope spec implemented"
```
