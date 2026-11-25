# curl + OpenAPI tools for eling-agent — design

Date: 2026-11-12
Status: approved (2026-11-12). Phase A (curl + Network page) first; Phase B (OpenAPI) follows.
Size: project (four new MCP tools, a network config store, a spec registry store,
  eight-ish API endpoints, two dashboard additions, core unit tests)

## Intent and why

Agents using eling-agent need first-class HTTP access. Today the only way to
make a network call is `shell_execute` with a `curl` command line, which is
RCE-equivalent and disabled by default. This change adds a lean, structured
HTTP tool (`curl`), two OpenAPI spec readers (`openapi_list`,
`openapi_operation`) that answer "what does this API look like", and an
imported-spec registry so the user manages a set of known OpenAPI documents
**from the dashboard** — import once (URL or file), name it, enable or
disable it, refresh it, delete it — and agents reference those by name.

## Goals

- `curl` — general HTTP client for agents: any URL, any method, custom
  headers, raw string body. Enabled by default. Output capped and logged.
- `openapi_list` / `openapi_operation` — read an OpenAPI 3.x spec (JSON or
  YAML) from (a) an enabled imported spec name, (b) a sandboxed filesystem
  path, or (c) an http/https URL — and expose operations in drill-down form.
- `openapi_specs` — list the imported specs (name, description, source,
  enabled, operation count) so agents can discover what is import-registered
  before calling the readers. Read-only.
- Imported-spec management from the dashboard:
  - OpenAPI page (new route `/dashboard/openapi`): import via URL or file
    upload with a name + optional description, list all imported specs
    (name, description, source, title/version, operation count, imported
    timestamp, enabled switch), **refresh** re-fetch for URL-sourced specs,
    **enable/disable** per spec, and **delete**.
  - Backed by `openapi-specs.json` index + spec payloads stored under the
    user-scope config directory, same persistence pattern as
    `shell-config.json`.
- Network page (new route `/dashboard/network`) for curl settings: default
  timeout, max response body bytes, max redirects, allowed/blocked host
  rules with per-rule enable switches.
- Both new tool groups appear on the existing Tools page in a new `network`
  group, individually and group toggleable.
- Unit tests in `Eling.Core.Tests` for the HTTP client, the network config,
  the OpenAPI reader, and the spec-registry sanitize rules.

## Non-goals

- No multipart/file upload **as a curl feature** (file upload exists only for
  the dashboard's spec import), no response-file download, no cookie jar, no
  proxy support, no streaming, no auth flows. If it starts to look like
  Postman, it is out.
- No request validation against a spec. The agent validates by reading the
  spec via `openapi_operation`; `curl` stays a dumb pipe.
- No `$ref` resolution across files or URLs. Internal refs only.
- No OpenAPI 2.0 (swagger) support — rejected with a clear error.
- No generated tools per operation. The spec is read, not materialized.
- No spec *content* editing — imported payloads are immutable snapshots;
  refresh replaces the content wholesale. Environments are separate,
  user-managed metadata and are freely editable.

## Anti-goals

- Do not gate `curl` behind an enable-switch. Enabled by default, like
  `file_read`. The Tools page can still disable it.
- Do not add host restrictions that are on by default. Host rules exist but
  are inert until the user restricts to allowed hosts or enables a blocked
  host.
- Do not let the OpenAPI tools become an execution engine. They read specs;
  they never send requests (refresh is a dashboard action, not an agent one).
- Do not silently fetch external `$ref`s. Never.
- Do not make import or refresh unavailable to the dashboard while agents
  can read the registry — the dashboard is the management surface, agents
  are read-only consumers.

## Decisions (user, 2026-11-12)

- General HTTP client drives the design, not localhost/webhook specifics.
  Any URL, no limits by default.
- Response body: returned as text; valid JSON normalized (parsed and
  re-emitted). Non-text bodies reported as metadata, never dumped.
- Tool name: `curl`. Enabled by default. Redirects followed, max 5; every
  redirect hop re-validated against host rules; cross-host redirects drop
  Authorization/Cookie/Proxy-Authorization.
- OpenAPI: JSON + YAML, OpenAPI 3.x only. Two readers, drill-down form.
- `openapi_operation` detail capped at 128 KB serialized, with a `truncated`
  flag keeping the summary section.
- Spec reference order for the readers: enabled imported spec name →
  filesystem path (file-tools sandbox) → http/https URL.
- Import is via dashboard: URL (backend fetches at import) or file upload;
  name + optional description; registry stored user-scope.
- One imported spec can carry **multiple environments**; the differentiator
  is the base URL. Environments are `{ name, baseUrl, isDefault, headers }`.
  Base URLs prefill from the spec's `servers:` at import, user-overridable
  per env. (User decision, 2026-11-12.)
- Dashboard management includes list / detail / refresh / enable-disable /
  delete, plus per-spec environment management (add/edit/remove envs,
  per-env name + base URL + default headers, set-as-default).
- Agents select an env with a separate `env` parameter on `openapi_list` /
  `openapi_operation`. The selected env's baseUrl and default headers are
  returned in the tool output — displayed to the agent by design.
  (User decision, 2026-11-12.)
- Approach B: logic in `Eling.Core`, thin MCP wrappers in `Eling.Backend`,
  dashboard pages + endpoints, unit tests in `Eling.Core.Tests`.

## Architecture

```
Eling.Core/Http/
  CurlClient.cs          # the request executor + response model
  NetworkConfig.cs       # record + HostRule, JSON context
Eling.Core/OpenApi/
  OpenApiSpecReader.cs   # JSON+YAML → unified model, internal $ref resolution
  OpenApiModels.cs       # records for list/detail/registry-projection output
Eling.Backend/Network/
  NetworkConfigStore.cs  # network-config.json, ShellConfigStore twin
Eling.Backend/OpenApi/
  ImportedSpecStore.cs   # openapi-specs.json index + payload files, store twin
Eling.Backend/Mcp/Tools/
  CurlTools.cs           # [McpServerToolType] curl
  OpenApiTools.cs        # [McpServerToolType] openapi_list, openapi_operation, openapi_specs
Eling.Backend/Endpoints/
  NetworkEndpoints.cs    # GET/PUT /api/network/config
  OpenApiSpecEndpoints.cs# /api/openapi-specs … (see below)
Eling.Backend/Tools/ToolCatalog.cs   # NetworkGroup = "network", four entries
Eling.Backend/Mcp/ServerInstructions.cs  # one line for the new tools
Dashboard: app/dashboard/network/{page.tsx,Network.tsx},
  app/dashboard/openapi/{page.tsx,OpenApi.tsx}, Tools.tsx group entry,
  side nav entries, GROUP_TITLES
```

`Eling.Core` holds everything testable; the Backend layer does MCP
plumbing, config persistence, and HTTP endpoints.

## Component contracts

### CurlClient (Eling.Core)

Static/shared `HttpClient` over `SocketsHttpHandler` with
`AllowAutoRedirect = false`. Manual redirect handling to enforce the cap and
method semantics. Only `http`/`https` schemes; anything else →
`invalid_argument`.

Request: `method` (default `GET`), `url`, `headers` (name→value), `body`
(raw string or null), `timeoutMs` (nullable), `maxBodyBytes` (nullable),
`maxRedirects` (nullable).

Response: `statusCode`, `reasonPhrase`, `finalUrl`, `redirectCount`,
`redirectLimitExceeded` (bool), `headers` (name→value, first value per
header), `contentType`, `body` (string or null), `bodyTruncated` (bool),
`isBinary` (bool), `contentLength` (long, from header when present),
`durationMs`, `timedOut` (bool).

Behavior:

- Redirects followed for 301/302/303/307/308 that carry `Location`.
  - 303: method → `GET`, body dropped.
  - 301/302 with a non-GET method: method → `GET`, body dropped (follow
    browser behavior; curl's `-L` does the same for POST).
  - 307/308: method and body preserved.
- Every redirect hop is re-validated before being followed: scheme must stay
  http/https and the host rules apply per hop (a redirect to a blocked host,
  or to a non-allowed host when `restrictToAllowedHosts` is on, is refused
  with `host_not_allowed` instead of followed). (User decision, 2026-11-12.)
- On a cross-host redirect, `Authorization`, `Cookie` and
  `Proxy-Authorization` are dropped from the re-sent headers. Other headers
  are re-sent. (User decision, 2026-11-12.)
- Redirect without `Location`: returned as the final response.
- Cap exceeded: the last 3xx is returned with `redirectLimitExceeded: true`
  — a result, not an error.
- Timeout: `timedOut: true`, `statusCode: 0`. A DNS/connection/TLS failure
  returns an error with code `request_failed` and the exception message.
- Body read is capped at `maxBodyBytes` (+1 byte to detect overflow). Over
  cap → `bodyTruncated: true`, body kept at the cap.
- Binary detection: body bytes that are not valid UTF-8 (or contain NUL
  outside text whitelist) → `body: null`, `isBinary: true`, metadata still
  reported. No base64.
- JSON normalization: if `contentType` is `application/json` or `+json*` and
  the capped body parses as JSON, re-emit normalized (compact) JSON. If
  parsing fails, return the raw text with a `bodyParseError: true` flag.
- One shared static `HttpClient` instance, created once per process.

### NetworkConfig (Eling.Core) and network-config.json

Record, JSON source-generated metadata, persisted at
`<user-scope>/config/network-config.json`:

```
defaultTimeoutSeconds : 30        (>= 1)
maxResponseBodyBytes  : 262144    (256 KB, >= 1)
maxRedirects          : 5         (0..20)
restrictToAllowedHosts: false
allowedHosts          : [HostRule]
blockedHosts          : [HostRule]
updatedAt             : DateTimeOffset
```

`HostRule`: `{ host, enabled, createdAt, updatedAt }`. `host` is matched
case-insensitively; `*.example.com` matches any subdomain of example.com;
`example.com` matches exactly that host (not subdomains). Blank hosts are
dropped on read and on mutation. A bare `*` rule is dropped on read and on
mutation. Scoped wildcards such as `*.example.com` are kept as written.
`blockedHosts` always apply (when enabled); `allowedHosts` are consulted
only when `restrictToAllowedHosts` is true. Blocked wins over allowed. If
`restrictToAllowedHosts` is true and no allowed host matches and the host is
not blocked → refused with code `host_not_allowed`. The same checks run on
every redirect hop (see CurlClient behavior).

The store (`NetworkConfigStore`) is a direct twin of `ShellConfigStore`:
private gate, temp-file + move/replace writes, `RefreshIfStale` stamp check
(LastWriteTimeUtc + length), corrupt file → last good snapshot, blank-rule
sanitize on every read, template created on first run.

### Imported-spec registry (Eling.Backend/OpenApi + Eling.Core payload type)

Storage, both under `<user-scope>/config/openapi-specs/`:

```
openapi-specs.json     # index: [ImportedSpecEntry]
<slug>.json|.yaml      # raw spec payload bytes, as imported/uploaded
```

`ImportedSpecEntry` (in the index):

```
name          : string   # unique, case-insensitive, slug [a-z0-9][a-z0-9-_]{0,63}
description   : string?   # optional, <= 500 chars
source        : { kind: "url", url } | { kind: "file", originalFileName }
enabled       : bool
title         : string?   # from spec info.title at import time
version       : string?   # from spec info.version at import time
operationCount: int       # computed at import/refresh
format        : "json" | "yaml"
operationIds  : []string  # normalized ids for tools/listing — cached at import
importedAt    : DateTimeOffset
updatedAt     : DateTimeOffset   # refresh time
fileName      : string           # payload file under openapi-specs/
environments  : [EnvironmentEntry]
```

`EnvironmentEntry`:

```
name        : string   # slug, unique per spec (same slug rules as spec names)
baseUrl     : string   # absolute http/https URL, e.g. https://api-dev.example.com/v1
isDefault   : bool     # exactly one env per spec is default
headers     : [ { name: string, value: string } ]  # default headers for this env
createdAt   : DateTimeOffset
updatedAt   : DateTimeOffset
```

Import-time seeding of environments:

- If the spec has a `servers:` array, each server with an **absolute**
  http/https URL seeds an env: name from the server `description` slugified
  when present, otherwise sequential `env-1`, `env-2`, … in spec order.
  The first seeded env is marked `isDefault`.
- Servers with relative URLs (starting with `/`) are skipped during seeding —
  they cannot form a base URL; the user adds the env manually.
- No `servers:` (or none usable) → no envs seeded; the user adds them.
- Seeded envs carry no default headers; the user edits them.

Rules enforced by the store, on every read and mutation:

- Name uniqueness (case-insensitive); invalid slug → 400.
- Name collision on import → `name_taken`.
- Imported payload must parse as OpenAPI 3.x (reuses `OpenApiSpecReader`'s
  validation at import time). Unsupported/invalid → the import is rejected,
  nothing is persisted, and the parser message is returned.
- `operations`/`operationIds` cached in the index so the list view does not
  re-parse payloads.
- Environment rules, enforced on every mutation: env names are unique per
  spec (case-insensitive slug); `baseUrl` must be absolute http/https
  (validated); header names trimmed non-empty, values unrestricted; exactly
  one env is `isDefault` — the store normalizes "none marked" to the first
  env and refuses a set where two are marked.
- Deleting an index entry deletes its payload file. Orphan payloads (file
  without index entry) are left untouched but logged.
- The index persists atomically like the shell/network configs (temp-file +
  move/replace, stamp-based cross-process refresh).
- Refresh revalidates and replaces the payload and re-derives title,
  version, operationCount and operationIds — it never touches
  environments, which are user-managed.

Fetch policy at import/refresh time (dashboard action, user-initiated):
an `url` must be http/https; fetch uses `CurlClient` with the
`maxSpecBytes` cap (default 4 MB) and the default redirect cap — the
**agent** host restrictions do **not** apply (the user explicitly pasted the
URL; it is the same trust as the user fetching it themselves). Document this
as a call. File uploads are capped at `maxSpecBytes` too. Over cap →
`spec_too_large`.

### Endpoints

Network config (existing §NetworkConfig):

```
GET  /api/network/config
PUT  /api/network/config      # full replacement; timeout>=1, maxResponseBodyBytes>=1, maxRedirects in 0..20
```

Imported specs:

```
GET    /api/openapi-specs                  # list index entries (no payloads);
                                            # each entry includes its environments with
                                            # header *counts* but not header values
GET    /api/openapi-specs/{name}           # one entry (environments with header names and
                                            # values — it is the user's own config page)
                                            # + its operation summaries
POST   /api/openapi-specs                  # multipart/form-data:
                                            #   fields: name, description?, and exactly one of url | file
PUT    /api/openapi-specs/{name}           # body: { enabled?: bool, environments?: [EnvironmentEntry] }
                                            # at least one field required; environments replaces the
                                            # whole set (validated: slug names unique per spec, absolute
                                            # http(s) baseUrl, exactly one isDefault — none marked →
                                            # first env becomes default; two marked → 400)
POST   /api/openapi-specs/{name}/refresh   # url-sourced only; 409 'not_url_source' otherwise
DELETE /api/openapi-specs/{name}           # removes index entry + payload file
```

The `GET /{name}` detail returns the entry plus `operations` (summaries) so
the dashboard detail view needs no extra round trip.

### MCP tools

`curl` — drafted description:

> Send an HTTP request and return `statusCode`, `finalUrl`, `redirectCount`,
> `headers`, `body` (JSON normalized when applicable), `bodyTruncated`,
> `isBinary`, `contentType`, `durationMs`. Follows redirects (max 5 by
> default); every redirect hop is re-validated against the network config's
> host rules. Any http/https URL. No credentials beyond what you put in
> `headers`. Response body is capped (default 256 KB); raise or lower
> `maxResponseBodyBytes` per call. When `restrictToAllowedHosts` is enabled,
> non-matching hosts are refused with `host_not_allowed`. Refused with a code
> before sending when: `url` is missing/invalid or the scheme is not
> http/https (`invalid_argument`), or the host is blocked/not allowed
> (`host_not_allowed`). A non-2xx status is a result, not an error.

Parameters: `method` (string, default `GET`), `url` (string, required),
`headers` (string→string, optional; the builder may fall back to a JSON
string parameter if the MCP schema generator cannot emit a dictionary),
`body` (string, optional), `timeoutMs` (int?, clamped [1000, 600000]),
`maxResponseBodyBytes` (int?, clamped [1024, 10_485_760]), `maxRedirects`
(int?, clamped [0, 20]). Missing arguments fall back to the network config
values. Returns the standard tool JSON envelope (`{ ok, ... }` on success,
`{ ok:false, error, code }` on failure), like `shell_execute`.

`openapi_specs` — no parameters. Returns `{ ok, specs: [{ name, description,
sourceKind, title, version, operationCount, enabled, importedAt, updatedAt,
environments: [{ name, baseUrl, isDefault, headerCount }] }] }` — the
registry projection: envs disclosed with their base URLs and header counts,
**never header values**. Read-only.

`openapi_list` — parameters: `spec` (string, required), `env` (string?,
optional — imported specs only; exact env name, else the spec's default
env; unknown env → `env_not_found` with the available names), `pathPrefix`
(string?, filter), `tag` (string?, filter), `maxOperations` (int?, default
100, clamped [1, 500]), `maxSpecBytes` (int?, default 4_194_304, clamped
[262144, 16777216]). Returns `{ ok, title, version, operationCount,
env, baseUrl, defaultHeaders, operations: [{ operationId, method, path,
summary, tags }], truncated }`. `env`/`baseUrl`/`defaultHeaders` are present
only when the spec came from the registry; `defaultHeaders` lists the
selected env's headers **in full** (user decision: shown to the agent).
Summaries otherwise.

`openapi_operation` — parameters: `spec` (string, required), `env` (same
semantics as `openapi_list`), `operationId` (string, required),
`maxSpecBytes` (same default/clamp). Returns `{ ok, env, baseUrl,
defaultHeaders, operationId, method, path, summary, description, parameters:
[...], requestBody: {...}, responses: {...} }` with internal `$ref`s
resolved; `env`/`baseUrl`/`defaultHeaders` only for registry-sourced specs.
The serialized detail is capped at 131072 bytes (128 KB); over the cap, the
tool returns the summary section plus `truncated: true`. (User decision,
2026-11-12.) Unknown operationId → `operation_not_found` plus a capped list
of the closest available ids (max 20). Operations without an `operationId`
are reachable as `METHOD /path` (normalized).

Spec reference resolution (`spec` parameter, both readers), in order:

1. **Imported spec name** — exact, case-insensitive match against the
   registry. Disabled entry → `spec_disabled`. Not found → fall through.
2. **Filesystem path** — resolves against the file-tools ProjectScope chain
   head (`chain.Head?.Root`); relative paths resolve there, and absolute
   paths must stay inside that sandbox (outside → `sandbox_violation`).
   File is read raw with `maxSpecBytes` cap; over → `spec_too_large`.
3. **http/https URL** — fetched via `CurlClient` with `maxSpecBytes` in
   place of the response cap, redirects and host rules apply; over →
   `spec_too_large`.
4. Otherwise → `invalid_argument`.

OpenAPI reading:

- OpenAPI 3.0/3.1 (`openapi:` top-level field). Missing → `not_openapi_document`.
  Swagger 2.0 (`swagger:`) → `unsupported_version`.
- JSON: parsed with `System.Text.Json` into `JsonNode`. YAML: parsed with
  YamlDotNet 18.1.0 (already referenced by `Eling.Core`) and converted into
  the same `JsonNode` shape.
- Internal `$ref` (`#/…`): resolved recursively at materialization time with
  a cycle guard (visited set + max depth 32). Where the guard trips, the
  output contains a raw `{ "$ref": "#/original/path" }` stub — never silently
  truncated, never an error. External refs — any ref not starting with `#` —
  are left as raw `{ "$ref": "…" }` objects, never fetched.
- If the document is not valid JSON/YAML, or required sections are missing,
  → `invalid_spec` with the parser message. Every catch logs, per the
  standing rule.

### Grouping, catalog, policy

- `ToolCatalog`: new `NetworkGroup = "network"` with `curl`,
  `openapi_list`, `openapi_operation`, `openapi_specs`. Group toggle works
  via the Tools page for free.
- `ToolPolicyStore`: no change — the four are enabled by default, none are
  protected.
- Tools page (`Tools.tsx`): add `"network"` to `GROUP_ORDER`,
  `GROUP_TITLES` ("Network Tools"), and the group description line.
- `ServerInstructions.cs`: one line — `curl` is the HTTP tool, the OpenAPI
  readers are read-only, `openapi_specs` lists imported specs; agents should
  check the registry before passing a filesystem path/URL.

### Dashboard Network page

Route `/dashboard/network`, modeled on the Shell page:

- Card "Default timeout (seconds)", number input, >= 1.
- Card "Max response body (bytes)", number input + human equivalent, >= 1.
- Card "Max redirects", 0..20.
- Card "Restrict to allowed hosts" master switch.
- Allowed hosts list — add/remove (optional leading `*.`), per-rule switch.
- Blocked hosts list — same.
- Save via `PUT /api/network/config` (full replacement, same contract as the
  shell PUT); toast on success/error like the Shell page.

### Dashboard OpenAPI page

Route `/dashboard/openapi`:

- Header + "Import spec" button opening a dialog with: `name` (validated
  against the slug rules client-side as well as server-side), optional
  `description`, and a source switch: **URL** (text field) or **File**
  (`.json`/`.yaml`/`.yml`, single file, <= 4 MB client-side check as a
  courtesy). Submit → `POST /api/openapi-specs` (multipart). Errors surface
  the server message (`spec_too_large`, `invalid_spec`, `name_taken`, …).
- Table/card list of imported specs: name, description, source (URL host or
  original file name), title/version, operation count, imported/updated
  timestamps, enabled switch (immediate `PUT /{name}`), actions menu:
  **Refresh** (only for URL sources; disabled with tooltip otherwise) and
  **Delete** (confirm dialog; file and index entry both removed).
- Detail drawer/view per spec: the cached operation summaries table (filter
  by prefix/tag) — enough to answer "is this the spec I want?" without
  leaving the page.
- Per-spec **environment manager** in the detail view: table of envs
  (name, baseUrl — full, default badge, header count, edit, delete), an
  "Add environment" dialog (name, baseUrl, default headers as key/value
  rows; a "prefill from servers" helper that fills baseUrl from the spec's
  first absolute server URL), and a set-as-default action per env. Header
  values are visible here — it is the user's own config page.
- Side nav entry "OpenAPI" next to "Network"/"Shell" (wherever the Shell
  entry is registered — the builder finds it).
- Enable/disable of the four MCP tools themselves stays on the Tools page.
  The OpenAPI page manages imported specs; the Network page manages curl
  behavior. (Call, noted for review.)

### Enforcement point for host rules

`curl` checks, in order: (1) empty/invalid URL → `invalid_argument`;
(2) scheme not http/https → `invalid_argument`; (3) any enabled blocked host
matches → `host_not_allowed`; (4) `restrictToAllowedHosts` on and no enabled
allowed host matches → `host_not_allowed`. Checks 3–4 happen before any
network I/O and are re-checked on every redirect hop. The OpenAPI readers
fetch remote specs through the same client and inherit the same checks.

### Logging

Every catch block logs (standing rule). `curl` logs refusals at Warning and
the request summary at Information (method, host, path, status, durationMs,
timedOut) — never the query string (it can carry secrets). OpenAPI readers
log fetch/parse failures at Warning. Import/refresh failures are logged and
also returned to the dashboard.

## Testing (Eling.Core.Tests)

`CurlClient` with a fake `HttpMessageHandler`:

- 200 JSON → body normalized, `isBinary` false; non-JSON body returned raw.
- Binary body → `body` null, `isBinary` true, metadata set.
- Body cap → truncated, `bodyTruncated` true.
- Redirect chain 302→200 → followed, `redirectCount` 1; 303 on POST → GET,
  body dropped; 307 on POST → method/body preserved; cap exceeded → last 3xx
  with `redirectLimitExceeded`; 3xx without Location → returned as-is.
- Timeout → `timedOut` true, status 0. Headers reach the handler and are
  surfaced.
- Redirect to a blocked host (or non-allowed host under restriction) →
  `host_not_allowed`; cross-host redirect drops
  Authorization/Cookie/Proxy-Authorization; same-host keeps them; redirect to
  a non-http(s) scheme → `invalid_argument`.

`NetworkConfig` sanitize: blank hosts dropped; bare `*` dropped; `*.` scoped
wildcards kept; trimming. `NetworkConfigStore`: corrupt file → last good
snapshot; blank rule dropped; atomic replace (mirror existing store tests).

`ImportedSpecStore` (Backend tests if the harness supports it, else Core
sanitize-only tests): slug validation; name-uniqueness case-insensitive;
blank description trimmed; operation count cached; refresh on file-sourced
entry rejected; delete removes payload file; corrupt index → last good
snapshot. Environment rules: names unique per spec; baseUrl must be
absolute http/https; two `isDefault` envs rejected; none marked → first
becomes default; header with blank name dropped. Import from a spec with a
`servers:` array seeds envs (absolute URLs only, first marked default,
relative servers skipped).

`OpenApiSpecReader` with fixture specs (JSON and YAML twins):

- List: count, summary projection, pathPrefix/tag filter, maxOperations cap
  + `truncated`. Detail: parameters/requestBody/responses materialize; over
  128 KB → `truncated: true`, summary kept.
- Internal `$ref` resolved (local + `#/components/…`); cycle emits raw stub;
  external `$ref` left raw; spec over `maxSpecBytes` → `spec_too_large`
  (local and remote paths).
- Swagger 2.0 → `unsupported_version`; missing `openapi` →
  `not_openapi_document`; malformed → `invalid_spec`.
- Missing operationId → `operation_not_found` + ≤ 20 suggestions; operation
  without operationId reachable as `METHOD /path`. Sandboxed spec path
  outside the file-tools root → `sandbox_violation`.

## Decisions left to the builder

- `headers` parameter type (dictionary vs JSON string) — whichever the MCP
  tool-schema generator accepts; behavior identical.
- YAML→`JsonNode` conversion mechanics.
- Compact vs pretty JSON for normalized bodies (proposal: compact).
- Multi-value response headers: first value; note in the description.
- Slug normalization edge cases for names beyond the documented rules.

## Calls the builder-check surfaced, now settled

- Redirect hops re-validated + cross-host credential stripping — user
  decision.
- Remote spec fetch uses `maxSpecBytes` (default 4 MB), not the 256 KB
  network cap — `spec_too_large` over cap.
- `openapi_operation` serialized detail capped at 128 KB with `truncated`.
- Multi-env per imported spec: `{ name, baseUrl, isDefault, headers }`;
  seeds from `servers:` at import (absolute URLs only); selected via `env`
  param on both readers; selected env's full default headers are shown to
  the agent — user decision. `openapi_specs` shows envs with header counts
  only, never values — call, veto if you want values there too (or nowhere).
- `$ref` cycle output: raw `{ "$ref": "#/…" }` stub in place.
- Spec paths resolve against the file-tools ProjectScope chain head; no
  external escape; URLs for anything else.
- Bare `*` host rule dropped on sanitize.
- Import/refresh-time fetch is **not** subject to agent host rules (the user
  initiated it), but is capped by `maxSpecBytes` and http/https only — call,
  veto if unwanted.
- New `openapi_specs` read-only discovery tool added so agents can see what
  is import-registered — call, veto if unwanted.
- Spec enable/disable lives on the OpenAPI page (per spec), not the Tools
  page; the four MCP tools' enable/disable stays on the Tools page.

## Open risks

- A general HTTP tool can reach internal services. The user accepted this
  ("any URL, no limits by default") with the dashboard host rules as the
  opt-in brake. The `curl` description must state plainly that it is
  unrestricted by default.
- Imported specs are user-trusted content; they are parsed, never executed.
- Default headers per env may contain secrets; they are stored as plaintext
  in the user-scope config by design and are returned in full to any agent
  that asks for the env (user decision). Anyone with read access to the
  agent's context can see them.
- The registry is read cross-process (dashboard + each MCP host) — the
  stamp-based refresh pattern is mandatory, same as the other stores.
