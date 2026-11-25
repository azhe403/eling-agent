# curl + Network dashboard (Phase A) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the `curl` MCP tool — a lean, enabled-by-default HTTP client for agents — plus the `network-config.json` store, `/api/network/config` endpoints, a dedicated Network dashboard page, and a `network` group on the Tools page.

**Architecture:** Core logic (`CurlClient`, `HostRuleMatcher`, `NetworkConfig` record) lives in `Eling.Core` and is unit-tested with a fake `HttpMessageHandler`. The Backend adds a config store (exact twin of `ShellConfigStore`), thin MCP/endpoints layers, and the dashboard pages. No external HTTP service is contacted by tests.

**Tech Stack:** .NET 10, System.Net.Http, source-generated JSON contexts (the backend publishes with Native AOT), xUnit, Next.js dashboard (App Router), `apiFetch` envelope helper.

**Spec:** `docs/superpowers/specs/2026-11-12-curl-openapi-tools-design.md` (sections: CurlClient, NetworkConfig + network-config.json, MCP tools → `curl`, Grouping/catalog/policy, Dashboard Network page, Enforcement point for host rules, Logging, Testing). This plan implements Phase A only; the OpenAPI tools and the imported-spec registry are Plan B.

## Global Constraints

- Target framework `net10.0`; `Eling.Core` already references `YamlDotNet` — do not add packages for Phase A.
- Every type serialized to disk needs a source-generated JSON context (`[JsonSerializable]`) registered; see `ShellJsonContext.cs` for the shape (SnakeCaseLower naming, PropertyNameCaseInsensitive, WriteIndented, camelCase→enum policy).
- Every catch block logs, per the standing rule. Refusals (host blocked, bad URL) log at Warning; the per-request summary logs at Information and never logs the URL query string (scheme + host + path only).
- `curl` is **enabled by default** (unlike `shell_execute`); it is a normal, non-protected tool in `ToolCatalog`. Never add an enable gate to the tool itself.
- Host rule semantics (copy verbatim from spec): `host` matched case-insensitively; `*.example.com` matches any subdomain of example.com but NOT `example.com` itself; `example.com` matches exactly; a bare `*` is dropped on sanitize; blocked wins over allowed; `allowedHosts` are consulted only when `restrictToAllowedHosts` is true.
- Redirect behavior (spec): 301/302/303 → `GET` with body dropped (303 always; 301/302 for non-GET); 307/308 preserve method+body; **every hop is re-validated** (scheme stays http/https + host rules apply); cross-host hop drops `Authorization`, `Cookie`, `Proxy-Authorization`; no `Location` → return the 3xx as-is; cap exceeded → last 3xx with `redirectLimitExceeded: true`.
- Response body captured capped at `maxBodyBytes` (default from config, 262144 bytes); `+1 byte` probe detects overflow → `bodyTruncated: true`.
- Non-text (invalid UTF-8) bodies are reported as metadata only: `body: null`, `isBinary: true`. Valid JSON (`contentType` contains `json`, case-insensitive) in capped text is re-emitted compact.
- `HttpClient` shared instance per process; tests construct their own with a fake handler.
- Backend builds into `ELING_OUTPUT_ROOT`; tests run via `pwsh scripts/test-backend.ps1 -Layer core|api|all` — never a bare `dotnet test` against the whole solution in the default folder, never `pnpm build` while the dev server is on 4427.
- Dashboard: all API calls go through `apiFetch` from `@/lib/api` (unwraps the standard envelope). New pages are client components under `src/app/dashboard/<name>/` with a `page.tsx` **server component** that exports `metadata` and renders the client component; follow `app/dashboard/shell/page.tsx` exactly.
- `openui-lang`/UI blocks not relevant here; when editing `Tools.tsx` keep its existing layout and add `"network"` to `GROUP_ORDER` + `GROUP_TITLES` (`"Network Tools"`) + the group description line.
- Commit style in this repo (see `git log --oneline`): `type(scope): summary`, e.g. `feat(backend): …`, `docs(shell): …`.

## Review Focus

1. A redirect that lands on a blocked host (or a non-allowed host under `restrictToAllowedHosts`) must be refused, not followed — pinned by `CurlClientTests.SendAsync_RedirectToBlockedHost_IsRefused` (Task 2) and `CurlToolsTests.ExecuteAsync_RedirectToBlockedHost_IsRefusedBeforeNetwork` (Task 5).
2. A cross-host redirect must strip `Authorization`/`Cookie`/`Proxy-Authorization`; a same-host redirect must keep them — pinned by `CurlClientTests.SendAsync_CrossHostRedirect_StripsCredentials` (Task 2).
3. Binary responses (invalid UTF-8 bytes) must not dump garbage into agent context — pinned by `CurlClientTests.SendAsync_BinaryBody_IsMetadataOnly` (Task 2).
4. Timeout must come back as a normal result (`timedOut: true`, `statusCode: 0`), never as an exception — pinned by `CurlClientTests.SendAsync_Timeout_ReturnsTimedOutResult` (Task 2).
5. A non-http(s) scheme (`file://`, `ftp://`, empty URL) must be refused with `invalid_argument` before any network I/O — pinned by `CurlClientTests.SendAsync_NonHttpScheme_IsRefused` and `CurlToolsTests.ExecuteAsync_InvalidUrl_ReturnsInvalidArgument` (Tasks 2, 5).

---

### Task 1: `NetworkConfig` record + `HostRuleMatcher` (Eling.Core)

**Files:**
- Create: `src/backend/Eling.Core/Http/NetworkConfig.cs`
- Test: `tests/Eling.Core.Tests/HostRuleMatcherTests.cs`

**Interfaces:**
- Consumes: nothing (first task).
- Produces:
  - `public sealed record HostRule(string Host, bool Enabled, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt);`
  - `public sealed record NetworkConfig { bool RestrictToAllowedHosts; List<HostRule> AllowedHosts; List<HostRule> BlockedHosts; int DefaultTimeoutSeconds = 30; int MaxResponseBodyBytes = 262144; int MaxRedirects = 5; DateTimeOffset UpdatedAt; }` with a `[JsonConstructor]` mirroring `ShellConfig` (sources defaults when the file omits a field; `UpdatedAt == default` → `DateTimeOffset.UtcNow`).
  - `public static class HostRuleMatcher { public static string NormalizeHost(string host); public static bool IsMatch(string host, string rule); }`

- [ ] **Step 1: Write the failing test**

Create `tests/Eling.Core.Tests/HostRuleMatcherTests.cs` with cases:
`NormalizeHost` trims, lowercases, strips one trailing dot; `example.com` vs `Example.COM.` → `example.com`.
`IsMatch("example.com", "example.com")` → true (case-insensitive).
`IsMatch("api.example.com", "example.com")` → false.
`IsMatch("api.example.com", "*.example.com")` → true.
`IsMatch("example.com", "*.example.com")` → false.
`IsMatch("deep.api.example.com", "*.example.com")` → true (any depth).
`IsMatch("notexample.com", "*.example.com")` → false (must not suffix-match `notexample.com`).
`IsMatch("anything.test", "*")` → false (bare `*` is inert at match time).
`IsMatch("EXAMPLE.COM", "example.COM")` → true (rule normalized too).
`IsMatch("", "example.com")` → false; `IsMatch("x", "")` → false.

- [ ] **Step 2: Run test to verify it fails**

Run: `pwsh scripts/test-backend.ps1 -Layer core`
Expected: FAIL — `NetworkConfig` / `HostRuleMatcher` do not exist (compile error).

- [ ] **Step 3: Implement**

Create `src/backend/Eling.Core/Http/NetworkConfig.cs`:

- `HostRule` — positional record as above. Host stored normalized (trimmed + lowercased) by callers, not the record itself.
- `HostRuleMatcher.NormalizeHost`: trim → lowerInvariant → strip a single trailing `.`. `IsMatch`: normalize both sides; empty either side → false; rule `*` (exactly) → false; rule starts with `*.` → true iff `host` is strictly longer than the rule suffix (`example.com` part) and ends with `.` + suffix (the `*.` matches one-or-more leading labels); otherwise ordinal-ignore-case equality.
- `NetworkConfig` — `[JsonConstructor]` with parameters `restrictToAllowedHosts = false`, `allowedHosts = null`, `blockedHosts = null`, `defaultTimeoutSeconds = 30`, `maxResponseBodyBytes = 262144`, `maxRedirects = 5`, `updatedAt = default`. Null lists → `[]`. `UpdatedAt == default` → `DateTimeOffset.UtcNow`. Plain get-only properties with init.

- [ ] **Step 4: Run test to verify it passes**

Run: `pwsh scripts/test-backend.ps1 -Layer core`
Expected: PASS (all HostRuleMatcherTests + existing suite green).

- [ ] **Step 5: Commit**

```bash
git add src/backend/Eling.Core/Http/NetworkConfig.cs tests/Eling.Core.Tests/HostRuleMatcherTests.cs
git commit -m "feat(core): add NetworkConfig record and HostRuleMatcher"
```

---

### Task 2: `CurlClient` (Eling.Core)

**Files:**
- Create: `src/backend/Eling.Core/Http/CurlClient.cs`
- Test: `tests/Eling.Core.Tests/CurlClientTests.cs`

**Interfaces:**
- Consumes: `NetworkConfig`, `HostRule`, `HostRuleMatcher` (Task 1).
- Produces:
  - `public sealed record CurlRequest(string Method, string Url, IReadOnlyDictionary<string, string>? Headers, string? Body, int? TimeoutMs = null, int? MaxBodyBytes = null, int? MaxRedirects = null);`
  - `public sealed record CurlResult { bool Ok; string? Error; string? Code; int StatusCode; string ReasonPhrase; string FinalUrl; int RedirectCount; bool RedirectLimitExceeded; IReadOnlyDictionary<string,string> Headers; string? ContentType; string? Body; bool BodyTruncated; bool IsBinary; long? ContentLength; long DurationMs; bool TimedOut; }` — use a record with init properties and a static `Error(code, message)` factory.
  - `public readonly record struct NetworkHostPolicy(bool RestrictToAllowedHosts, IReadOnlyList<HostRule> AllowedHosts, IReadOnlyList<HostRule> BlockedHosts);` with `public static NetworkHostPolicy FromConfig(NetworkConfig config)` (enabled rules only).
  - `public sealed class CurlClient` — public ctor `CurlClient(HttpMessageHandler handler, NetworkConfig? configDefaults = null)` for tests + `public static CurlClient Shared { get; }` (one `SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }` + `HttpClient`, no handler disposal).
  - `public async Task<CurlResult> SendAsync(CurlRequest request, NetworkHostPolicy hostPolicy, CancellationToken cancellationToken = default)`

- [ ] **Step 1: Write the failing tests**

Create `tests/Eling.Core.Tests/CurlClientTests.cs`. A `FakeHandler : HttpMessageHandler` queueing `HttpResponseMessage`s and recording requests (`List<HttpRequestMessage>`); responses built inline. Tests (name → key assertions):

- `SendAsync_200Json_ReturnsNormalizedCompactBody` — contentType `application/json`, body `{"a": 1, "b": [1,2]}` → `Ok` true, body `{"a":1,"b":[1,2]}`, `IsBinary` false, `Truncated`/`bodyParseError` false, `StatusCode` 200, headers surfaced.
- `SendAsync_200PlainText_ReturnsBodyRaw` — `text/plain` `hello` → body `hello`.
- `SendAsync_InvalidJson_ReturnsRawBodyWithParseErrorFlag` — `application/json` body `{oops` → body `{oops`, `BodyParseError` true. (Add `BodyParseError` to `CurlResult`.)
- `SendAsync_BinaryBody_IsMetadataOnly` — contentType `application/octet-stream`, bytes `FF D8 FF E0 00 10 ...` (invalid UTF-8) → `Body` null, `IsBinary` true, `ContentType`/`ContentLength` reported.
- `SendAsync_BodyOverCap_TruncatesAndFlags` — body 3000 bytes with cap 1024 → body length 1024, `BodyTruncated` true.
- `SendAsync_302Redirect_FollowedAndCounted` — first response 302 `Location: https://b.example/final`, second 200 → `FinalUrl` `https://b.example/final`, `RedirectCount` 1, `StatusCode` 200.
- `SendAsync_303OnPost_ReissuedAsGetWithoutBody` — 303 after POST → second request method GET, content null.
- `SendAsync_301OnPost_ReissuedAsGet` — same for 301.
- `SendAsync_307OnPost_KeepsMethodAndBody` — second request POST with original body.
- `SendAsync_RedirectWithoutLocation_Returns3xxAsIs` — 302 with no `Location` → `StatusCode` 302, `RedirectCount` 0.
- `SendAsync_RedirectCapExceeded_ReturnsLast3xxWithFlag` — 4× 302 in a row with cap 3 → `StatusCode` 302, `RedirectCount` 3, `RedirectLimitExceeded` true.
- `SendAsync_RedirectToBlockedHost_IsRefused` — hostPolicy with enabled blocked rule `blocked.example`; first response 302 → `Location: http://blocked.example/`; expect `Ok` false, `Code` `host_not_allowed`, and **the handler recorded exactly one request** (never followed).
- `SendAsync_RedirectToNonAllowedHostUnderRestrict_IsRefused` — `RestrictToAllowedHosts = true`, allowed = `allowed.example`; redirect to `other.example` → `Code` `host_not_allowed`, one request only.
- `SendAsync_CrossHostRedirect_StripsCredentials` — initial host `a.example` with `Authorization`, `Cookie`, `Proxy-Authorization` and `X-Custom: keep`; redirect to `b.example` → second request drops the three, keeps `X-Custom`. Same-host redirect → all headers kept.
- `SendAsync_RedirectToNonHttpScheme_IsRefused` — redirect to `file:///c:/x` → `Code` `invalid_argument`, not followed.
- `SendAsync_Timeout_ReturnsTimedOutResult` — handler delays (e.g. `Task.Delay(5000)`) with `TimeoutMs = 1000` → `TimedOut` true, `StatusCode` 0, `Ok` true (timeout is a result, not an error). Use a fake handler that respects the linked token only if convenient; a simple un-cancellable delay works with the client's `CancelAfter`.
- `SendAsync_NonHttpScheme_IsRefused` — `file:///etc/passwd` → `Ok` false, `Code` `invalid_argument`. `ftp://a/b` same. Empty URL → same.
- `SendAsync_NetworkFailure_ReturnsRequestFailed` — handler throws `HttpRequestException` → `Ok` false, `Code` `request_failed`.
- `SendAsync_HeadersReachHandler_AndResponseHeadersSurfaced` — request header `X-Test: v` arrives; response headers (`ETag`) appear in `CurlResult.Headers`.
- `SendAsync_CallerCancellation_Propagates` — caller-token canceled → `OperationCanceledException` escapes (mirrors shell_execute semantics).
- `SendAsync_HostClamping_TakesConfigDefaults` — `MaxBodyBytes`/`MaxRedirects`/`TimeoutMs` null → falls back to the `NetworkConfig` the client was constructed with (or 30s/262144/5 when none).
- Review Focus 2–5 above map to tests `SendAsync_CrossHostRedirect_StripsCredentials`, `SendAsync_BinaryBody_IsMetadataOnly`, `SendAsync_Timeout_ReturnsTimedOutResult`, `SendAsync_NonHttpScheme_IsRefused`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `pwsh scripts/test-backend.ps1 -Layer core`
Expected: FAIL — type `CurlClient` does not exist.

- [ ] **Step 3: Implement `CurlClient`**

Key mechanics (write exactly this):

1. Validate: URL parses (`Uri.TryCreate` Absolute), scheme in {`http`,`https`} (case-insensitive) else `Error("invalid_argument", "url must be an absolute http(s) URL: <url>")`.
2. Build the initial request; per-redirect loop up to `maxRedirects`; track `finalUrl`, `redirectCount`.
3. `HostAllowed(host)`: blocked (enabled rules, `HostRuleMatcher.IsMatch`) → false; `RestrictToAllowedHosts` && no enabled allowed rule matches → false; else true. Check before every hop including the first; failure → `Error("host_not_allowed", $"host '{host}' is blocked by the network allow/block rules")` (before any I/O for the first request; before following for redirects).
4. Resolve effective limits: `TimeoutMs ?? config.DefaultTimeoutSeconds*1000` (int clamp [1000,600000]), `MaxBodyBytes ?? config.MaxResponseBodyBytes` (clamp [1024, 10485760]), `MaxRedirects ?? config.MaxRedirects` (clamp [0,20]).
5. Linked `CancellationTokenSource.CreateLinkedTokenSource(ct)` with `CancelAfter(timeout)`; on `OperationCanceledException` when caller token set → rethrow; else `TimedOut` result.
6. Redirects: 301/302/303 with `Location` → build next request as GET (drop body) except 307/308 (keep method+body); 301/302/303 always GET. Parse `Location` against current URL (`Uri.TryCreate(current, location, out next)`); invalid → return current response as-is. Scheme must remain http/https; host policy re-check; cross-host → remove `Authorization`, `Cookie`, `Proxy-Authorization` from the next request's headers.
7. No `Location` header on a 3xx → stop and return the response.
8. Body: read `response.Content` stream with a cap of `maxBodyBytes + 1`; `contentLength` from `Content-Length` header when parseable. If bytes count `> maxBodyBytes` → truncate to cap, `BodyTruncated = true`.
9. Text/binary: strict decode attempts (`new UTF8Encoding(false, true).GetString(bytes)`); exception → `IsBinary = true`, `Body = null`. Else text; if `ContentType` contains `json` → `JsonNode.Parse`; success → `node.ToJsonString()` (compact); failure → keep raw + `BodyParseError = true`.
10. Error handling: `catch (HttpRequestException ex)` and `catch (IOException/InvalidOperationException)` → `Error("request_failed", ex.Message)` (+ `_logger` if an `ILogger` is available — accept optional `ILogger<CurlClient>`; log at Warning). Do not swallow caller `OperationCanceledException`.
11. `DurationMs` from `Stopwatch` around the whole loop.

- [ ] **Step 4: Run tests to verify they pass**

Run: `pwsh scripts/test-backend.ps1 -Layer core`
Expected: PASS (all CurlClientTests + suite green).

- [ ] **Step 5: Commit**

```bash
git add src/backend/Eling.Core/Http/CurlClient.cs tests/Eling.Core.Tests/CurlClientTests.cs
git commit -m "feat(core): add CurlClient with capped redirects and host rules"
```

---

### Task 3: `NetworkConfigStore` + JSON context (Eling.Backend)

**Files:**
- Create: `src/backend/Eling.Backend/Network/NetworkConfigStore.cs`
- Create: `src/backend/Eling.Backend/Network/NetworkJsonContext.cs`
- Test: `tests/Eling.Backend.Tests/Network/NetworkConfigStoreTests.cs`

**Interfaces:**
- Consumes: `NetworkConfig`, `HostRule`, `HostRuleMatcher` (Task 1).
- Produces: `NetworkConfigStore` (public sealed, ctor `(UserScope, ILogger<NetworkConfigStore>)`) with `GetConfig()`, `Update(Func<NetworkConfig, NetworkConfig>)` — **exact behavioral twin of `ShellConfigStore`**: private `_gate`, `RefreshIfStale()` stamp check (LastWriteTimeUtc + length), temp-file + `File.Move(overwrite: true)` write, corrupt file → last-good snapshot, template written on first run, `UpdatedAt` stamped in code on every mutation.
- Sanitize (on every read and every mutation): drop rules whose `Host` is blank after trim; drop rule with host exactly `*`; normalize host via `HostRuleMatcher.NormalizeHost`; trim; clamp `DefaultTimeoutSeconds >= 1`, `MaxResponseBodyBytes >= 1`, `MaxRedirects` into [0, 20].

- [ ] **Step 1: Write the failing tests**

Create `tests/Eling.Backend.Tests/Network/NetworkConfigStoreTests.cs`, modeled on `tests/Eling.Backend.Tests/Shell/ShellConfigStoreTests.cs` (`TempWorkspace`-style root, `NullLogger<NetworkConfigStore>`). Cases:

- Fresh store → template exists at `<root>/config/network-config.json`, defaults: timeout 30, body bytes 262144, maxRedirects 5, `RestrictToAllowedHosts` false, empty lists.
- `Update` that adds an entry persists atomically and round-trips through a **new** store instance.
- Corrupt file (write `"not json"` over it) → `GetConfig()` returns the last good snapshot (or template), file left untouched, no throw.
- Rules with blank host dropped; rule `*` dropped; `*.example.com` kept; host normalized (`"  EXAMPLE.com. "` → `example.com`).
- Out-of-range values clamped on sanitize (`DefaultTimeoutSeconds = 0` → persisted as `>= 1` — pick: keep value but endpoints validate; decide: sanitize clamps to minimums; assert clamp behavior).
- Two `Update`s from two store instances — second instance sees first's write (the stamp reload).
- Write failure leaves `_config` swapped only after success (hard to inject; skip if it complicates — note as accepted).

- [ ] **Step 2: Run tests to verify they fail**

Run: `pwsh scripts/test-backend.ps1 -Layer api`
Expected: FAIL — store does not exist.

- [ ] **Step 3: Implement**

Copy the structure of `ShellConfigStore.cs` (lines 1–296) changing only:`FileName = "network-config.json"`, the config type, `Sanitize` for host rules, and the two `ShellJsonContext.Default.ShellConfig` references → `NetworkJsonContext.Default.NetworkConfig`. `NetworkJsonContext.cs` mirrors `ShellJsonContext.cs` with `SnakeCaseLower` for wire names? **No** — `ShellConfig` serializes with `SnakeCaseLower`, and the dashboard DTO is camelCase via envelope middleware? Check: dashboard talks camelCase (`defaultTimeoutSeconds`) and `ShellConfig` on the wire is camelCase... Wait — `ShellConfig` properties are PascalCase in C#; the JSON context uses `SnakeCaseLower` and `ShellEndpoints` returns `TypedResults.Ok(store.GetConfig())` — but the dashboard expects `restrictToAllowedDirectories`. So how does ShellConfig get camelCase? Because HTTP JSON responses use the app's `JsonSerializerOptions` (web defaults via `Results.Ok`), NOT the source context — source context is only for the file. Let me verify: minimal API `Results.Ok(obj)` uses `JsonSerializerDefaults.Web` (camelCase) by default in ASP.NET Core. Yes. So `ShellConfig` → camelCase over HTTP, snake_case on disk. Keep the same split: `NetworkJsonContext` (SnakeCaseLower, for the file), endpoints return the record directly (Web defaults, camelCase).

- [ ] **Step 4: Run tests to verify they pass**

Run: `pwsh scripts/test-backend.ps1 -Layer api`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/backend/Eling.Backend/Network tests/Eling.Backend.Tests/Network/NetworkConfigStoreTests.cs
git commit -m "feat(backend): add network-config.json store"
```

---

### Task 4: `/api/network/config` endpoints + DI + route mapping

**Files:**
- Create: `src/backend/Eling.Backend/Endpoints/NetworkEndpoints.cs`
- Modify: `src/backend/Eling.Backend/Mcp/McpServiceExtensions.cs` (two spots: the primary `AddElingCoreServices` overload next to `AddShellConfigStore`, and the `ShellSessionRoot` overload next to line ~230; add `AddNetworkConfigStore` twin of `AddShellConfigStore`)
- Modify: `src/backend/Eling.Backend/Bootstrap/DashboardRoutes.cs` (add `app.MapNetworkEndpoints();` after `MapShellEndpoints()`)
- Test: `tests/Eling.Backend.Tests/Endpoints/NetworkEndpointsTests.cs`

**Interfaces:**
- Consumes: `NetworkConfigStore` (Task 3), `NetworkConfig` (Task 1).
- Produces: `public static class NetworkEndpoints { public static WebApplication MapNetworkEndpoints(this WebApplication app); }` with `GET /api/network/config` and `PUT /api/network/config` (full replacement, WebJson camelCase body; validation: `DefaultTimeoutSeconds >= 1`, `MaxResponseBodyBytes >= 1`, `MaxRedirects` in [0, 20] → else `Results.BadRequest`). On PUT, compute per-rule `UpdatedAt` stamps on enable flips exactly like `ShellEndpoints.PackWithStamp` (`ChangedStamp` by host, case-insensitive — never by index), and let the store's `Update` set the top-level `UpdatedAt`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Eling.Backend.Tests/Endpoints/NetworkEndpointsTests.cs`, modeled on `tests/Eling.Backend.Tests/Endpoints/ShellEndpointsTests.cs` (self-contained `TestAppBuilder.CreateSelfContained` app on a loopback port, `EnvelopeTestHandler`, `IXxxCollection` pattern if the base uses one). Cases:

- `GetConfig_OnEmptyState_ReturnsDefaultTemplate` — 30 / 262144 / 5, `RestrictToAllowedHosts` false, empty lists.
- `PutConfig_ToggleRestrict_PersistsAndRoundTrips` — PUT `restrictToAllowedHosts: true` with two allowed hosts, one disabled → GET back matches; `UpdatedAt` advanced.
- `PutConfig_OutOfRange_ReturnsBadRequest` — `DefaultTimeoutSeconds = 0` and `MaxRedirects = 21` → 400, config unchanged.
- `PutConfig_BlankOrStarRules_Dropped` — PUT with a blank host and `*` → response keeps only the valid rule.
- `PutConfig_ReorderedEnabledFlip_DoesNotMatchByIndex` — same reorder trick as the shell test: one rule starts disabled, rotate the list, flip; only the actually flipped rule gets `UpdatedAt != null`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `pwsh scripts/test-backend.ps1 -Layer api`
Expected: FAIL — route not mapped (404).

- [ ] **Step 3: Implement**

Mirror `ShellEndpoints.cs` (validation helper `PackWithStamp` for host rules, `ChangedStamp` by `Host`). In `McpServiceExtensions.cs`, add `private static void AddNetworkConfigStore(IServiceCollection services, UserScope userScope)` identical to `AddShellConfigStore` but constructing `NetworkConfigStore`, and call it next to `AddShellConfigStore` in both overloads. In `DashboardRoutes.Map`, add `app.MapNetworkEndpoints();` after `app.MapShellEndpoints();`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `pwsh scripts/test-backend.ps1 -Layer api`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/backend/Eling.Backend/Endpoints/NetworkEndpoints.cs src/backend/Eling.Backend/Mcp/McpServiceExtensions.cs src/backend/Eling.Backend/Bootstrap/DashboardRoutes.cs tests/Eling.Backend.Tests/Endpoints/NetworkEndpointsTests.cs
git commit -m "feat(backend): add /api/network/config endpoints"
```

---

### Task 5: `curl` MCP tool + catalog + instructions + tools-page group

**Files:**
- Create: `src/backend/Eling.Backend/Mcp/Tools/CurlTools.cs`
- Modify: `src/backend/Eling.Backend/Tools/ToolCatalog.cs` (add `public const string NetworkGroup = "network";` + entry `new("curl", NetworkGroup, "Send an HTTP request and return status, headers, and body (JSON normalized). No enable gate; capped by network-config.json."),`)
- Modify: `src/backend/Eling.Backend/Mcp/ServerInstructions.cs` (one line, see step 3)
- Modify: `src/frontend/Eling.Dashboard/src/app/dashboard/tools/Tools.tsx` (add `"network"` to `GROUP_ORDER`, `GROUP_TITLES` → `"Network Tools"`, and the `{group === "network" && "Send HTTP requests and work with remote APIs."}` description line — match existing wording pattern)
- Test: `tests/Eling.Backend.Tests/Mcp/CurlToolsTests.cs`

**Interfaces:**
- Consumes: `CurlClient`, `CurlRequest`, `CurlResult`, `NetworkHostPolicy.FromConfig` (Task 2), `NetworkConfigStore` (Task 3).
- Produces: `public sealed class CurlTools` with `[McpServerToolType]`, auto-discovered via `WithToolsFromAssembly()`. Tool name `curl`. Public ctor `(NetworkConfigStore store, ILogger<CurlTools>? logger)` — creates its own static(per-process) `CurlClient.Shared`; internal ctor overload additionally takes `CurlClient client` for tests (repo has `InternalsVisibleTo` for `Eling.Backend.Tests`).

- [ ] **Step 1: Write the failing tests**

`tests/Eling.Backend.Tests/Mcp/CurlToolsTests.cs`, modeled on `ShellToolsTests.cs` (temp root, plain store). Cases:

- `ExecuteAsync_EmptyUrl_ReturnsInvalidArgument` — `""`, `"   "` → `{ ok:false, code: invalid_argument }`.
- `ExecuteAsync_NonHttpScheme_ReturnsInvalidArgument`.
- `ExecuteAsync_BlockedHost_IsRefusedBeforeNetwork` — store seeded with an enabled blocked rule `blocked.example`; request to `http://blocked.example/` → `Code` `host_not_allowed`, and the injected fake handler recorded **zero** requests.
- `ExecuteAsync_RestrictedAndNotAllowed_ReturnsHostNotAllowed` — `restrictToAllowedHosts: true` on the store config; host not in list → refused pre-flight.
- `ExecuteAsync_HappyPath_ReturnsResultEnvelope` — fake handler returns 200 `application/json`; result JSON deserializes with `ok == true`, `statusCode == 200`, `body` compact.
- `ExecuteAsync_HeadersDictionary_RoundTrips` — `headers: {"X-Test": "v"}` reaches the handler.
- `ExecuteAsync_MethodDefaultsToGet_AndCanPost`.
- Review Focus 1 and 5 map here: `…_RedirectToBlockedHost_IsRefusedBeforeNetwork` (one pre-flight check is enough — the redirect path is covered by Task 2 tests) and `ExecuteAsync_InvalidUrl_ReturnsInvalidArgument`.
- `ExecuteAsync_MissingStoreDefaults_ClampParams` — `timeoutMs` below 1000 is clamped to 1000 (or rejected per your chosen clamp strategy — pick clamp and assert it, consistent with shell_execute's clamp choice).

- [ ] **Step 2: Run tests to verify they fail**

Run: `pwsh scripts/test-backend.ps1 -Layer api`
Expected: FAIL — `CurlTools` missing.

- [ ] **Step 3: Implement**

`CurlTools.ExecuteAsync`:

1. Parameters: `string? url` (required — check null/blank first → invalid_argument), `string method = "GET"`, `Dictionary<string, string>? headers = null` (if the MCP schema generator cannot emit a `Dictionary<string,string>`, fall back to `string? headersJson` parsed with `JsonNode` — try the dictionary first; verify with the smoke build), `string? body = null`, `int? timeoutMs = null`, `int? maxResponseBodyBytes = null`, `int? maxRedirects = null` (clamp all three: [1000, 600000] / [1024, 10_485_760] / [0, 20]).
2. Load `store.GetConfig()`; build `NetworkHostPolicy.FromConfig`.
3. Call `CurlClient.SendAsync(...)`; map `CurlResult` → JSON: success → `{ ok:true, statusCode, reasonPhrase, finalUrl, redirectCount, redirectLimitExceeded, headers, body, bodyTruncated, isBinary, contentType, contentLength, durationMs, timedOut, bodyParseError }` (omit null values); failure (`Ok == false`) → `{ ok:false, error, code }`. **Never send the query string to the log**; log Information `curl {Method} {Host}{Path} -> {StatusCode} ({DurationMs} ms, timedOut: {TimedOut})` — build Host/Path from the *original* URI, not the final one after redirects.
4. Standing rule: every catch logs.
5. `[McpServerTool(Name = "curl"), Description("Send an HTTP request and return statusCode, finalUrl, redirectCount, headers, body (JSON normalized when applicable), bodyTruncated, isBinary, contentType, durationMs. Follows redirects (max 5 by default); every redirect hop is re-validated against network-config.json host rules. Any http(s) URL — unrestricted by default. No credentials beyond what you put in headers; response body capped (default 256 KB, raise with maxResponseBodyBytes). Refused with invalid_argument for missing/non-http(s) url, host_not_allowed when blocked/not in the allow-list, request_failed for network errors. A non-2xx status is a result, not an error.")]`.
6. `ServerInstructions.cs`: add after the shell_execute line:
   `` `curl` makes HTTP requests (GET/POST/… ) with any http(s) URL; unrestricted by default, capped and logged; host rules in network-config.json can restrict it.``
7. ToolCatalog + Tools.tsx changes as listed above.

- [ ] **Step 4: Run tests to verify they pass**

Run: `pwsh scripts/test-backend.ps1 -Layer api`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/backend/Eling.Backend/Mcp/Tools/CurlTools.cs src/backend/Eling.Backend/Tools/ToolCatalog.cs src/backend/Eling.Backend/Mcp/ServerInstructions.cs src/frontend/Eling.Dashboard/src/app/dashboard/tools/Tools.tsx tests/Eling.Backend.Tests/Mcp/CurlToolsTests.cs
git commit -m "feat(backend): add curl MCP tool and network tool group"
```

---

### Task 6: Dashboard Network page + sidebar entry

**Files:**
- Create: `src/frontend/Eling.Dashboard/src/app/dashboard/network/page.tsx` (server component, `metadata` with `pageTitle("Network")`, renders `<Network />`)
- Create: `src/frontend/Eling.Dashboard/src/app/dashboard/network/Network.tsx` (client component; intro card with `Globe` icon stating: `curl` is enabled by default, unrestricted by design, host rules are the opt-in brake, settings live in `network-config.json`)
- Create: `src/frontend/Eling.Dashboard/src/app/dashboard/network/NetworkConfigCard.tsx` (client component mirroring `ShellConfigCard.tsx`, minus the enable toggle and boundary switch; contents below)
- Modify: `src/frontend/Eling.Dashboard/src/components/app-sidebar.tsx` (add `{ title: "Network", url: "/dashboard/network", icon: <Globe className="size-4" /> },` directly after the Shell entry; import `Globe`)

**Interfaces:** Consumes `GET/PUT /api/network/config` (Task 4) and the `apiFetch` envelope unwrap (established pattern).

`NetworkConfigDto` (camelCase, as the endpoints emit): `{ restrictToAllowedHosts: boolean; allowedHosts: Array<{ host: string; enabled: boolean; createdAt: string; updatedAt: string | null }>; blockedHosts: Array<…same>; defaultTimeoutSeconds: number; maxResponseBodyBytes: number; maxRedirects: number; updatedAt: string }`.

- [ ] **Step 1: Write the failing check**

There is no component test runner for this pattern in the repo (dashboard `test` = vitest; existing pages have no tests). Verification = type check + grep-parity review. Add the checklist as a steps block:
- `pnpm --filter eling-dashboard exec tsc --noEmit` — expected: errors only from the new files' missing exports until implemented; after implementation: clean.

- [ ] **Step 2: Implement**

`NetworkConfigCard.tsx` — copy the structure of `ShellConfigCard.tsx` with these deltas:
- DTO above; load via `apiFetch<NetworkConfigDto>("/api/network/config")`.
- Fields: `restrictToAllowedHosts` ToggleRow ("Restrict to allowed hosts" / "On: curl may only reach hosts matched by the allowed list below"); number inputs for `defaultTimeoutSeconds` (>= 1), `maxResponseBodyBytes` (>= 1, helper text shows e.g. "262144 = 256 KB"), `maxRedirects` (0..20); `RuleList` twice for allowed/blocked hosts with placeholder `api.example.com`/`*.example.com` — reuse the same `RuleList` pattern (a copy inside this file is fine; do not refactor the shell card).
- Optimistic save + revert-on-error pattern copied from `ShellConfigCard.commit` / `save`; number commit via `commitNumber` analog; per-rule toggles stamp nothing client-side — server handles ChangedStamp by host.
- Toast handling: follow the same `saveError` inline pattern as the shell card.
`Network.tsx` — intro Card (icon `Globe`) + `<NetworkConfigCard />`.
`app-sidebar.tsx` — insert the Network item after Shell; note that the backend must be restarted/built before real traffic sees the page on its route (dev proxy forwards `/api/*` only — the page itself is static SPA).

- [ ] **Step 3: Run the verification**

Run: `pnpm --filter eling-dashboard exec tsc --noEmit`
Expected: clean. (Do NOT run `pnpm build` — the dev server on 4427 owns `.next/`.)

- [ ] **Step 4: Commit**

```bash
git add src/frontend/Eling.Dashboard/src/app/dashboard/network src/frontend/Eling.Dashboard/src/components/app-sidebar.tsx
git commit -m "feat(dashboard): add Network page for curl settings"
```

---

### Task 7: Full verification + README note

- [ ] **Step 1:** Run `pwsh scripts/test-backend.ps1 -Layer all` (core + api + desktop). Expected: all pass. If an unrelated flaky test fails (known: `RecordAsync_WhenTheChannelBacksUp_NeverDropsAnEntry` is flaky), retry once and note it — do not "fix" it in this plan.
- [ ] **Step 2:** `pnpm --filter eling-dashboard exec tsc --noEmit` — clean.
- [ ] **Step 3:** Smoke the MCP tool surface: `dotnet run --project src/backend/Eling.Backend` is NOT allowed without user opt-in on the 4417 dev port (it owns stdin); instead verify the tool appears in `ToolCatalog.All` via `dotnet` reflection smoke or an existing catalog test if one exists — if no test exists, add a tiny xunit in `Eling.Backend.Tests` asserting `ToolCatalog.Exists("curl")` and `MembersOf("network")` is `["curl"]`.
- [ ] **Step 4:** README — extend the `Operational tooling` line with `, and an enabled-by-default \`curl\` HTTP tool with host allow/block rules`.
- [ ] **Step 5: Commit**

```bash
git add README.md
git commit -m "docs(readme): note the curl MCP tool"
```
