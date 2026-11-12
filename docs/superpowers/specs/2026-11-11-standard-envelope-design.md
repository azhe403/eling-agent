# Standard Response Envelope — Design

- **Date:** 2026-11-11
- **Status:** Implemented (2026-11-12) - middleware, endpoint polish, `apiFetch` migration, Desktop client; all test layers green (core 754, backend 1057, frontend 182, desktop 108). Live smoke against 4417 skipped by user decision; TestServer reproduces the full pipeline.
- **Scope:** `src/backend/Eling.Backend` (pipeline + a few endpoint polish spots), `src/frontend/Eling.Dashboard/src` (API helper + ~74 call-site migration), `src/desktop/Eling.Desktop` (`Services/ElingApiClient.cs` + one more file; 21 `ReadFromJsonAsync`/`GetFromJsonAsync` sites migrated via a `ReadEnvelopeAsync<T>` unwrap helper, choice (a) — user-picked)
- **Extends:** `2026-11-11-no-empty-endpoint-responses-design.md` (now absorbed and deleted). The no-empty-response guarantee and the envelope are ONE middleware, not two buffering passes.

## The no-empty-body guarantee (absorbed from the old spec)

History: the user first reported `GET /api/databases/{id}/schema` answering **404 with a zero-byte body** — undebuggable client-side. The rule, now enforced by the same envelope middleware:

- **≥ 400** → always a JSON body: `{"code": "<lower_snake_case>", "message": "<human sentence>", "data": null}`, regardless of the endpoint's Content-Type (raw strings, JSON, no-content-type — all normalized into `message`).
- **2xx with an empty body** → `{"code": "ok", "message": "<verb-derived default, e.g. \"The request was processed successfully by the server.\" on POST>", "data": null}` (e.g. bare `Results.Ok()`). Never a zero-byte 200.
- **Unhandled exception** → `{"code": "internal_error", "message": "...", "data": null}` with 500 (the pipeline previously had NO exception handler — empty 500).
- **204, 304, HEAD** remain empty — the HTTP spec mandates it.
- `MapFallbackToFile` for unmatched `/api/*` stays untouched (user's explicit call); envelope wraps what the pipeline and endpoints produce.

## Intent / Why

The user wants every endpoint response to share one predictable shape so the API is tidy and clients can trust it. Today the backend returns ~30 distinct raw shapes (DTOs, bare `Ok()`, `StatusCode(403)`, raw strings, problem+json), and each of the frontend's 74 `fetch()` call sites hand-parses whatever it gets, with `parseApiError(text)` as the rickety common path.

## Decisions (as picked by the user)

1. **Full envelope**, not error-only. Three root fields, `data` last:
   - Success: `{ "code": "ok", "message": "<verb-derived default or domain sentence>", "data": <payload> }`
   - Failure: `{ "code": "<lower_snake_case>", "message": "<human sentence>", "data": null }`
   - HTTP status codes keep their meaning (envelope is the body, not a replacement for status).
2. **`message` is always populated, and it is *prose for the user*, not a label.** On success it is the endpoint's domain sentence when one is opted in (`HttpContext.Items["successMessage"]`); otherwise the middleware derives a default from the HTTP verb — `GET`/`HEAD` → "The resource was read successfully from the server.", `POST`/`PUT`/`PATCH` → "The request was processed successfully by the server.", `DELETE` → "The resource was deleted successfully by the server.", anything else → "The request was completed successfully by the server." (all ≥ 6 words; the 6-word bar applies to success messages too — they are read by humans as well). On failure it is a full sentence: what happened, plus what to do (or where to look). **Minimal bar:** a failure message must (a) say what happened and why, and (b) tell the user the next step or where to look — reviewers check this question, not a word counter; the operational floor is **6 words** (enough for cause + next step), a floor not a target, and the verb-derived defaults already meet it (`ok` is a `code` token, not a message). **Why 6 and not 5 or 10:** below 6 words it is nearly impossible to carry both the cause and the next step — a sentence like "Connection not found" (3 words) states the outcome but leaves the reader with nothing to do; 5 is still too tight in practice. Above 6, the count stops being a minimum and becomes style — from there on the author owns the register, and the reviewer checks substance, not length. Status-derived fallbacks are pre-enriched prose (e.g. 404 → "The resource you asked for no longer exists — it may have been removed; refresh the list and try again."), not "Not Found". When an endpoint already wrote a human sentence (e.g. `LogsEndpoints`' "Unknown stream 'foo'." or a layered validation message), it is reused as-is. Writing rules for new messages: English (base strings; domain messages may localize), one sentence, states the cause and the next step, includes the identity asked for (id/name) when it helps, never exposes credentials. `code` carries the machine identity (`connection_not_found`); `message` carries the story for the human.
3. **`code` stands at the root, equal to `message`**, and is **always filled** — `ok` on success, a `lower_snake_case` identity of the failure on ≥400 (user refinement: every field of the envelope is always populated). Style: `lower_snake_case` (`connection_not_found`) — chosen over `UPPER_SNAKE` (enum-constant style, noisier in payloads, gRPC idiom not JSON API idiom) and over `kebab-case`/`camelCase` (not identifier-safe / not idiomatic for a value). Generic codes derived from status (`not_found`, `bad_request`, `forbidden`, `conflict`, `unsupported_media_type`, `internal_error`, `ok`, ...); domain codes on the endpoints polished in this or earlier work (`connection_not_found`, `path_outside_root`, `binary_file`, ...). All codes in this API are lower_snake_case — one convention, no exceptions.
4. **Approach A — boundary transform** (user-picked over explicit call-site envelopes and over a frontend dual-read transition).
5. **Exempt from the envelope (non-error path):** SSE (`text/event-stream`), static files/SPA, `204`/`304`/`HEAD`, and any non-JSON content type on SUCCESS. Error responses are enveloped regardless of content type. `/health` **is** enveloped like everything else (no special-casing).
6. **Unmatched `/api/*` stays untouched** (`MapFallbackToFile` keeps serving the SPA) — user's explicit call.
7. **Unhandled exceptions** return `{ "code": "internal_error", "message": ..., "data": null }` with 500. `ex.Message` is exposed, matching the existing convention (loopback app).
8. **Key order: `code`, `message`, `data`.** `data` (the long payload: lists, schemas, query results) stays at the bottom; the two short-circuit fields lead with `code` — a client glancing at the top line sees the machine identity, then the human sentence. Serialization order is controlled by the middleware (`JsonNode`/manual writer), not by property declaration order scattered through endpoints. Clients read by property name (`body.data` / `body.message` / `body.code`), so order is invisible to them.

## How

### Backend — one middleware, no call-site rewrites

`ApiEnvelopeMiddleware` registered FIRST in `DashboardRoutes.Map(app)` (before static files / correlation / routing, same slot the no-empty guard was planned for):

1. Swap `Response.Body` for a `MemoryStream`; run the pipeline; catch any endpoint exception.
2. Pass-through is decided **at first write** (wrapper stream that inspects content type), not after the pipeline completes — otherwise buffered `FlushAsync` never releases SSE frames. Then:
   - If method is `HEAD`, or status is `204`/`304` → copy through untouched.
   - If **status ≥ 400** → always enveloped: `{ "code": <generic-or-domain>, "message": <normalized>, "data": null }`. Any content type — a raw-string error body (e.g. `LogsEndpoints` writing a bare reason) or JSON string/object/plain text — is normalized into `message`; a JSON object with `message`/`detail`/`error` (string form) supplies the sentence; the `code` is the endpoint's domain code when one was chosen, otherwise the status-derived generic.
   - Else if `Content-Type` is not `application/json` (SSE, static, text) → copy through untouched (success-path non-JSON stays raw).
   - Else (JSON success) → `{ "code": "ok", "message": <domain sentence or verb-derived default>, "data": <original JSON> }`.
   - Malformed JSON on a JSON-typed response → `500 internal_error` envelope + log (never a mangled pass-through).
3. **Empty-body rule preserved:** 2xx with empty body → `{ "code": "ok", "message": <verb-derived default>, "data": null }`; error with empty body → `{ "code": ..., "message": <enriched prose for that status>, "data": null }`. Never a zero-byte response except 204/304/HEAD.
4. Content-Type rewritten to `application/json; charset=utf-8`; `Content-Length` recomputed.

Domain codes are opt-in per endpoint via `HttpContext.Items`. Polish in this pass (few files):
- `Endpoints/DatabaseEndpoints.cs` — `ConnectionNotFound` sets `connection_not_found`; message upgraded to full prose ("Connection '<id>' is gone — it may have been deleted in another tab or by another eling app; the connection list is being refreshed.").
- `Endpoints/RedisKeyRoute.cs` — `NotFound` sets `connection_not_found` (keyspace misses keep their existing ex.Message).
- `Endpoints/AgentWorkspaceEndpoints.cs` — 403s set `path_outside_root`, 415 sets `binary_file`; messages upgraded to full prose idioms (what happened + what to do).

Everything else gets status-derived generic codes for free — 35 bare `NotFound()` sites unchanged and already safe.

### Frontend — one helper, mechanical migration

New `src/frontend/Eling.Dashboard/src/lib/api.ts`:

```ts
export class ApiError extends Error {
  readonly status: number
  readonly code: string
}
export async function apiFetch<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(path, init)
  const body = await res.json().catch(() => null)
  if (!res.ok) throw new ApiError(res.status, body?.code ?? genericCode(res.status), body?.message ?? `HTTP ${res.status}`)
  return body?.data as T
}
```

Migration (mechanical, file by file, ~74 sites in ~30 files):
- `const res = await fetch(...); if (!res.ok) throw new Error(parseApiError(await res.text())); const x = await res.json()` → `const x = await apiFetch(...)`.
- `.text()` error branches disappear (ApiError carries the message).
- `res.status === NNN` checks (14 sites) → `err instanceof ApiError && err.status === NNN`.
- `parseApiError` becomes unused; delete it and its special-cases (`mutations not allowed...`) — the message now travels at the root of the envelope, already humanized by the backend.
- `useSchema.ts` `SchemaFetchError` is replaced by `ApiError` (keep the 404 → `onConnectionMissing` path).
- Files to migrate: list enumerated in the branch diff (databases/*, memories/*, judge/*, workspaces/*, codebase/*, shell/*, tools/*, logs/*, home/*, create/*, settings/*, audit-trail/*, components/*, hooks/*).

No change to SSE consumers (`EventSource`), no change to notification/toast plumbing beyond message extraction (toasts read `ApiError.message` — the root `message`).

### Compatibility notes (verified)

- MCP does not speak HTTP to this app (stdio transport) — envelope is invisible to agents' tool layer.
- Peer backends call `/api/coordinator/*` with fire-and-forget bodies they do not parse — unaffected by envelope.
- No binary/file-download responses exist in the API (verified: no `Results.File`/streams-as-responses), so the "JSON only" transform cannot mangle payloads.
- `/api/events/*` are SSE streams — exempt by content type.
- Dev proxy (`next.config.ts` `/api/*`) unchanged.

## Wire examples

All HTTP status semantics unchanged; the envelope only wraps the body. Ids/pids/messages are illustrative; structure and field/code names are normative.

Success with payload — `GET /api/databases`:

```json
{
  "code": "ok",
  "message": "The resource was read successfully from the server.",
  "data": [
    {
      "id": "01m7qb4x51tpdqh8rx0l6zcnp2",
      "name": "northwind_sales@stg-mysql.internal.example.com",
      "provider": "MySql",
      "environment": "ReadOnly",
      "hasConnectionString": true,
      "allowAgentMutations": false,
      "requireApprovalForMutations": true,
      "createdAt": "2026-10-26T08:24:54.622954+00:00",
      "lastConnectedAt": "2026-11-03T13:21:38.0732305+00:00",
      "details": {
        "host": "stg-mysql.internal.example.com",
        "port": 3306,
        "database": "northwind_sales",
        "username": "northwind_sales_usr",
        "filePath": null
      },
      "isServerLevel": false
    },
    {
      "id": "01m7qb4x51tpdqh8rx0l6zcnp3",
      "name": "northwind_analytics@stg-mysql.internal.example.com",
      "provider": "MySql",
      "environment": "ReadOnly",
      "hasConnectionString": true,
      "allowAgentMutations": true,
      "requireApprovalForMutations": false,
      "createdAt": "2026-10-28T03:06:51.3094034+00:00",
      "lastConnectedAt": null,
      "details": {
        "host": "stg-mysql.internal.example.com",
        "port": 3306,
        "database": "northwind_analytics",
        "username": "northwind_analytics_usr",
        "filePath": null
      },
      "isServerLevel": false
    }
  ]
}
```

Success with a domain message — e.g. a mutation route that opted in (`HttpContext.Items["successMessage"]`):

```json
{
  "code": "ok",
  "message": "Connection '01m5vn00jyt…' was deleted successfully.",
  "data": null
}
```

Success, single object — `GET /api/health`:

```json
{
  "code": "ok",
  "message": "The resource was read successfully from the server.",
  "data": {
    "status": "Healthy",
    "pid": 51220,
    "version": "0.9.1+7c259f5",
    "uptimeSeconds": 4821
  }
}
```

Success with a rich payload — `POST /api/databases/{id}/query` with `SELECT * FROM customers LIMIT 3`:

```json
{
  "code": "ok",
  "message": "The request was processed successfully by the server.",
  "data": {
    "columns": ["customer_id", "customer_name", "created_at", "credit_limit"],
    "rows": [
      [101, "Adventure Works", "2026-08-30T04:12:00Z", 50000],
      [102, "Contoso Ltd", "2026-09-14T09:41:00Z", 75000],
      [103, "Northwind Traders", "2026-10-02T17:03:00Z", 25000]
    ],
    "truncated": true,
    "source": "live"
  }
}
```

Success, no body — bare `Results.Ok()` (e.g. `POST /api/coordinator/register`):

```json
{
  "code": "ok",
  "message": "The request was processed successfully by the server.",
  "data": null
}
```

404 with domain code — `GET /api/databases/{id}/schema` for an unknown id (polished in this pass):

```json
{
  "code": "connection_not_found",
  "message": "Connection '01m601q4b3xgrcz29p2bjptfez' is gone - it may have been deleted in another tab or by another eling app; the connection list is being refreshed.",
  "data": null
}
```

404 bare — any of the ~35 untouched `Results.NotFound()` sites (generic code from status):

```json
{
  "code": "not_found",
  "message": "The requested resource does not exist (anymore) - refresh the connection list and try again.",
  "data": null
}
```

400 bad request — `POST /api/databases/{id}/query` with a refused statement:

```json
{
  "code": "bad_request",
  "message": "Only read-only statements are allowed here - write operations are not permitted on this connection.",
  "data": null
}
```

403 path escapes workspace — `GET /api/agent/files/list?root=C:\Projexts` (polished in this pass):

```json
{
  "code": "path_outside_root",
  "message": "That path is outside the workspace root - open a file inside one of the registered workspaces.",
  "data": null
}
```

415 binary file — `GET /api/agent/files/read` on a binary (polished in this pass):

```json
{
  "code": "binary_file",
  "message": "That file looks like a binary - this read tool only returns text, open it in a hex viewer.",
  "data": null
}
```

500 unexpected exception — any endpoint that throws uncaught:

```json
{
  "code": "internal_error",
  "message": "The database connection string is malformed - fix it in the dashboard connection form and try again.",
  "data": null
}
```

Error from a raw-string, non-JSON endpoint — e.g. `LogsEndpoints` writing a bare reason with no Content-Type; enveloped anyway, raw string becomes `message`:

```json
{
  "code": "bad_request",
  "message": "Unknown log stream 'foo' - pick one of the streams this app actually exposes (see the log viewer).",
  "data": null
}
```

204 No Content — `DELETE /api/databases/{id}` success: body **empty** (HTTP-mandated, not enveloped).

SSE — `GET /api/events/memories`: not JSON, not enveloped:

```
data: connected

: ping

data: memory_saved
```

Success non-JSON — e.g. a `text/plain` 200: passed through raw, not wrapped.

Client-side (dashboard, `apiFetch`): all of the above collapse into one path — success returns `body.data` directly; any ≥400 throws `ApiError` with `status`/`code`/`message`, so call sites and toasts read the backend's sentence instead of "Request failed.". The desktop client (`ReadEnvelopeAsync<T>`) unwraps the same shape for its 21 call sites.

## Tests

- Backend (`NoEmptyResponseTests` / new `ApiEnvelopeTests`, TestServer, pattern of `DatabaseManualConnectionTests`):
  1. Bare `Results.Ok(dto)` route → 200, `{ code: "ok", message: "<verb-derived default>", data: dto }`.
  2. Bare `Results.NotFound()` route → 404, `{ code: "not_found", message: "<enriched prose>", data: null }`.
  3. Endpoint that sets `HttpContext.Items["errorCode"]` → that code travels in root `code`.
  4. Endpoint that throws → 500 `{ code: "internal_error", message, data: null }`.
  5. `text/plain` **error** route (raw-string body, no JSON) → enveloped, raw string lands in `message`. `text/plain` **success** route → body raw, not wrapped. 204 → empty stays. SSE route → `data:` frames untouched.
  6. `DatabaseManualConnectionTests` keep passing (status assertions unchanged) — plus one assertion that `/api/databases/missing/schema` now carries the envelope.
- Desktop (`Eling.Desktop.Tests` if present, else build-only verification): the 21 migrated call sites deserialize envelope-unwrapped payloads correctly (unit/sample reads of `/api/memories` shape and a 404 path).
- Frontend (vitest, happy-dom): `lib/api.test.ts` — apiFetch unwraps `data`, throws ApiError with status/code/message on ≥400 (code from root `body.code`, message from root `body.message`), on non-envelope JSON throws `invalid_response`.
- Verification: lite backend filter (`~Database|~Redis|~NoEmpty|~ApiEnvelope`), `tsc --noEmit`, live `curl` showing envelope on a 404 and on a 200 (e.g. `/api/health`).

## Builder-check resolution (2026-11-11)

- **SSE trap:** the middleware decides pass-through at first write (wrapper stream inspecting content type / headers), not after `next()` completes — otherwise buffered `FlushAsync` never releases SSE frames and every live stream breaks.
- **apiFetch 2xx contract:** a 2xx whose body is not the envelope → `ApiError(invalid_response)`. A `204`/empty body → `undefined` (callers use it as `void`). `home/data.ts`'s stale-backend detection keeps working: old backends answer SPA-HTML 200 or 404, both of which now surface as `ApiError` (status/code inspectable) instead of silent misparse; `isMissingRoute` migrates to the ApiError shape. The doc's own test list previously contradicted the sketch; the sketch wins: `apiFetch` throws `invalid_response`.
- **Rule refinement:** **errors are enveloped regardless of content type** — a raw-string or untyped-content-type error body becomes `{ code, message: <raw string>, data: null }`. Success-path non-JSON bodies (200 text/plain, files) are copied through raw, exempt. This closes the "raw error loses its message" hole with no endpoint edits and no `.text()` fallback in the helper.

## Decisions (continued, from user)

- **Desktop is in scope** — choice (a): `src/desktop/Eling.Desktop` is a live HTTP client of this API, so the envelope would otherwise break it silently (all-default DTOs / empty arrays). The desktop client migrates in the same pass with a matching `ReadEnvelopeAsync<T>` unwrap helper (21 sites, ~2 files). Same rule for its tests.
- **`code` promoted to the root, equal to `message`** (user-picked over nesting under `error`): `{ code, message, data }`. `code` is **always filled** — `ok` on success, lower_snake_case on failure (user refinement: every field is always populated). The nested `error` object is gone entirely - an `error` that only carried `code` was a needless wrapper.
- **Success message is verb-derived, not "ok"** (user-picked over accepting the duplicated "ok"/"ok" and over leaving message empty on success): the middleware fills it from the HTTP verb — GET/HEAD → "Resource read successfully.", POST/PUT/PATCH → "Request processed successfully.", DELETE → "Resource deleted successfully.", else "Request completed successfully." — or from `HttpContext.Items["successMessage"]` when the endpoint opts in.

## Success criteria

No JSON response from the backend lacks the envelope; no frontend or desktop call site reads a raw response outside `apiFetch`/`ReadEnvelopeAsync<T>`; dead-body 404s are impossible; error toasts show the backend's root `message` (human sentence) instead of "Request failed."; Desktop reads the same API without silent data loss.

## Risks / calls the builder may make

- **Buffering**: every response pays one copy. Fine at current sizes.
- **`parseApiError` deletion**: a couple of call sites relied on the special-cased strings; root `message` from the backend is already human, so behavior is preserved.
- **Controllers vs minimal APIs**: transform applies to both (same pipeline); controller `ChallengeResult`/redirects have no JSON body and pass through unchanged.
- **Domain codes beyond the polished set**: generic codes elsewhere — deliberate, not a defect.
- **`successMessage` opt-in**: no endpoint is required to set one; the default is the verb-derived sentence, so wiring stays free. Endpoints that care (mutations) may set it.
- **Messages are prose, not contract**: tests assert presence of a non-empty human sentence (and its shape), never exact wording — `code` is the stable identity; rewording `message` is a non-event.
