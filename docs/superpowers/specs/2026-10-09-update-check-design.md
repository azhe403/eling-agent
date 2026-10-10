# Occasional Update Check & Offer Specification

**Date:** 2026-10-09
**Status:** Draft (Awaiting User Review)
**Scope:** `Eling.Backend`, `Eling.Dashboard`, install scripts, test suites

---

## 1. Overview & Goals

Eling runs as a local binary installed from the `azhe403/eling-agent` GitHub releases. Today nothing tells the user when a new release ships, so users silently fall behind.

This specification adds an **occasional update check**: while Eling is in use, the backend periodically queries the GitHub Releases API, compares the newest tag against the running binary version, caches the result locally, and offers the user an update without disrupting the workflow.

Goals:
1. Check for new releases periodically without blocking startup and without telemetry.
2. Phase 1: the dashboard snackbar notifies, the chat agent executes. The toast informs
   and links the release; the actual update runs only in chat after explicit user approval,
   where the agent runs the OS installer and verifies.
3. Respect anonymous GitHub rate limits, offline mode, and an explicit opt-out.
4. No silent auto-update and no one-click dashboard apply in phase 1 — execution stays
   in chat where approval and failure handling are conversational.
   Phase 2 adds a one-click apply button to the toast.

---

## 2. Architecture & Components

```
┌────────────────────────────────────────────────────────┐
│              GitHub Releases API (public)              │
│   GET /repos/azhe403/eling-agent/releases?per_page=20  │
│   pre-release channel, ignore drafts                 │
└───────────────────────────┬────────────────────────────┘
                            │ HTTPS (HttpClient, 10s timeout)
                            ▼
┌────────────────────────────────────────────────────────┐
│                   Eling Backend Engine                 │
│                                                        │
│  ┌──────────────────────────────────────────────────┐  │
│  │ UpdateCheckerService (BackgroundService, owner)  │  │
│  │ - 12h interval + 0-10 min startup jitter        │  │
│  │ - fail-soft: offline/429/timeout uses cache     │  │
│  │ - respects ELING_DISABLE_UPDATE_CHECK           │  │
│  └──────────┬───────────────────────────▲───────────┘  │
│             │                           │              │
│      read   │                      write│ cache        │
│             ▼                           │              │
│  ┌────────────────────────┐    ┌────────┴───────────┐  │
│  │ FileUpdateCache        │    │ GitHubReleaseClient│  │
│  │ <user-scope>/config/   │    │ User-Agent: eling  │  │
│  │ update-check.json      │    │ Accept: vnd.github │  │
│  └──────────┬─────────────┘    └────────────────────┘  │
│             │                                          │
│             │ UpdateStatus record                      │
│             ▼                                          │
│  ┌─────────────────────┐      ┌────────────────────┐    │
│  │ UpdateController    │      │ memory_recall embeds │    │
│  │ GET api/update/     │      │ an `update` block   │    │
│  │ status (Controller) │      │ (present only when  │    │
│  └─────────┬───────────┘      │ an update exists);  │    │
│            │                 │ agent appends a     │    │
└────────────┼──────────────────┴──────────┬───────────────┘
             │              (phase 1:       │ stdio
             │               notify only,  │
             │               no apply btn) │
             ▼                            ▼
┌─────────────────────────┐    ┌────────────────────────┐
│ Dashboard snackbar      │    │ Agent / MCP Client     │
│ toast: informs + links  │    │ ends its reply with a│
│ release, points user    │    │ short update note;   │
│ to chat for update      │    │ runs update on yes   │
└─────────────────────────┘    └────────────────────────┘
```

No dedicated `check_update` MCP tool: the recall response the agent already fetches
at session start carries the notice, so no extra tool, call, or prompt-context cost.

Only the dashboard owner performs network fetches. MCP-only instances read the cache so there is no duplicate traffic. This matches the existing owner-only watcher pattern: directory watchers and any polling work are registered only in owner mode, and a pure-MCP session with no watcher activity is expected by design rather than treated as a bug.

---

## 3. Storage & Domain Model

### 3.1 File Location & Persistence
- Location: `<user-scope>/config/update-check.json` (machine-local, alongside `tools-policy.json` and `semantic-judge.json`).
- Naming: all JSON fields use `snake_case` (for example `current_version`, `update_available`, `checked_at`), following the repository config standard.
- Timestamps: `checked_at` is always generated in backend code (`DateTimeOffset.UtcNow`) at the end of a successful check or cache mutation. Client-supplied timestamps are never trusted.
- Safe writes: serialize to a `.tmp` file first, then `File.Move` with overwrite, so a crash never leaves a half-written cache. The in-memory snapshot swaps only after the file move succeeds.
- Resilience: missing or corrupted files fall back to an unknown state without throwing.

### 3.2 Schema Definition
```json
{
  "current_version": "0.1.0",
  "latest_version": "0.2.0",
  "update_available": true,
  "release_url": "https://github.com/azhe403/eling-agent/releases/tag/v0.2.0",
  "release_notes": "short excerpt",
  "checked_at": "2026-10-09T08:00:00.0000000Z",
  "channel": "prerelease"
}
```

### 3.3 Version Compare (pure, no I/O)
- Strip a leading `v`, then strip any `+` build metadata (CI stamps `+sha.{shortsha}`
  into `InformationalVersion`; metadata never affects precedence).
- Split the semver core from the pre-release suffix (`-pre.N`).
- Compare the core numerically per segment. A stable tag outranks a pre-release tag on the same core. Numeric `-pre.N` suffixes compare numerically when possible.
- Examples: `v0.2.0` > `v0.1.0`; `v0.1.0` > `v0.1.0-pre.5`; `v0.1.0-pre.12` > `v0.1.0-pre.9`.

```csharp
namespace Eling.Backend.Updates;

public sealed record UpdateStatus(
    string CurrentVersion,
    string? LatestVersion,
    bool UpdateAvailable,
    string? ReleaseUrl,
    string? ReleaseNotes,
    DateTimeOffset CheckedAt,
    string Channel);

public static class SemanticVersionCompare
{
    public static int Compare(string left, string right);
    public static bool IsNewer(string candidate, string current);
}
```

The running version comes from the entry assembly (`InformationalVersion`) with a fallback to the `Directory.Build.props` base version (`0.1.0`).

---

## 4. Check Policy & Throttling

- Default interval is 12 hours plus 0-10 minutes of startup jitter (randomized per process so many machines do not hit the API at the same moment).
- The cache suppresses repeat calls: two rapid `CheckNow` invocations produce a single HTTP request, and a fresh TTL means serving the cache.
- Fail-soft behavior: a 10-second timeout, offline network, 403/429 responses, or unexpected JSON return the last known status and reschedule without errors. Startup never throws because of this path.
- Opt-out: `ELING_DISABLE_UPDATE_CHECK=1` or `true` disables everything (no fetch, no toast, and recall carries no `update` block).
- Channel: `prerelease` while no stable release exists. The checker selects the newest non-draft release (stable or pre-release); drafts are always ignored. This matches the installer fallback behavior in `scripts/install.ps1`, pinned to the pre-release side until the first stable tag ships. The channel stays configurable so it can flip to `stable` later without a code change.

---

## 5. REST & MCP Surface

### 5.1 REST Controller (`api/update/status`)
New endpoints in this repository are Controllers, not minimal APIs. Minimal-API parameter inference is group-wide rather than per-route, so one badly inferred parameter fails the whole group at startup. The dashboard host already mirrors JSON options for controllers, so no extra JSON configuration is needed.

- **Controller**: `UpdateController` (`[ApiController]`, `[Route("api/update")]`) following the `SystemController` pattern.
- **`GET api/update/status`**: returns `UpdateStatusDto` with camelCase serialization supplied by the shared MVC JSON options. Always 200. Before the first check, `latestVersion` is null and `updateAvailable` is false.
- Test hosts must register the backend assembly via `AddApplicationPart` or controller routes return 404, because MVC discovers controllers from the entry assembly.

### 5.2 Recall-Embedded Notice (no dedicated MCP tool)

There is deliberately no `check_update` MCP tool. The agent already calls `memory_recall`
at every session start, so the notice rides on that response: `MemoryRecallResponse` gains
an optional `update` block that is serialized only when an update is available and omitted
otherwise (mirroring how `projectScope` already travels inside the recall payload).

```csharp
// Added to MemoryRecallResponse (Eling.Backend/Dtos/MemoryRecallResponse.cs)
[JsonPropertyName("update")]
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public UpdateNoticeDto? Update { get; set; }

// Eling.Backend/Dtos/UpdateNoticeDto.cs
public sealed class UpdateNoticeDto
{
    [JsonPropertyName("currentVersion")]
    public string CurrentVersion { get; set; } = string.Empty;

    [JsonPropertyName("latestVersion")]
    public string LatestVersion { get; set; } = string.Empty;

    [JsonPropertyName("releaseUrl")]
    public string ReleaseUrl { get; set; } = string.Empty;

    [JsonPropertyName("installHint")]
    public string InstallHint { get; set; } = string.Empty;
}
```

The block is populated from the same `IUpdateChecker` cache the REST controller serves,
so it is at most as stale as the dashboard and costs no extra network call. Caller
convention for agents: when the `update` block is present, end the reply with a short
update note in Indonesian (current versus latest, install command, release link) placed
after the actual answer, mentioned once per conversation.

Example offer text (Indonesian):
> "Eling v0.2.0 tersedia (kamu di v0.1.0). Update? Jalankan: `irm https://raw.githubusercontent.com/azhe403/eling-agent/main/scripts/install.ps1 | iex` — atau lihat https://github.com/azhe403/eling-agent/releases/tag/v0.2.0"

### 5.3 Execution on Approval

When the user says yes, the agent runs the OS one-line installer from `INSTALL.md`,
then verifies with the `agent-setup.md` Step 4 probes (binary reachable, MCP stdio
handshake, dashboard health when a runtime owns the port). Windows file-lock rule: the
running backend locks its own binary, so if the installer reports a locked file, the
agent must NOT kill any process (never `taskkill /IM` on eling images). Instead it asks
the user to stop the staging backend or close the desktop client holding it, then retries.
The agent reports completion with the same shape as the install report in `agent-setup.md`.

---

## 6. Dashboard Snackbar (Phase 1: notify only)

The dashboard surfaces the update as a snackbar toast, not a persistent banner. It reuses
the established `ToastItem` pattern from the tools page (`src/frontend/Eling.Dashboard/src/app/dashboard/tools/page.tsx`):
a fixed top-right stack, at most a few items, auto-dismiss after a few seconds, success/error
styling with a manual dismiss button.

- A shared update-toast hook polls `GET api/update/status` once per hour and on mount.
- When `updateAvailable` is true and the version was not dismissed, it pushes one toast:
  title with the new version, body with the "View release" link (`releaseUrl`) and a
  line pointing the user to chat ("reply 'update eling' to the agent to install it").
  There is deliberately no apply button in phase 1 — execution stays in chat where
  approval and failure recovery are conversational. The one-click apply arrives in phase 2.
- Dismissal is per version through `localStorage` (`eling-dismissed-update=v0.2.0`). A newer
  version pushes again; the same version never re-pushes within its TTL window.
- The toast never blocks page content and never steals focus.

---

## 7. Verification & Testing Plan

Run only the tests a change touches. The backend suite is large and slow, so filtered runs are the norm here. Whenever a new C# file is added, include the code-organization convention tests. The dashboard frontend has no test runner, so validate it with lint and typecheck instead of proposing a new runner.

1. **Unit tests** (`tests/Eling.Backend.Tests/Updates/UpdateCheckTests.cs`):
   - Version-compare theory: stable versus pre-release, `v` prefix handling, numeric pre-release ordering, equality, `+sha` metadata ignored.
   - Channel selection: mixed draft/stable/pre-release input on the `prerelease` channel yields the newest non-draft release; drafts never win.
   - Throttling: two rapid `CheckNow` calls produce one HTTP request.
   - Corrupt or missing cache falls back to unknown without throwing.
2. **Controller tests**: `GET api/update/status` returns 200 with the expected DTO shape, following the existing controller test pattern.
3. **Recall block tests**: `memory_recall` omits the `update` block when no update is available and carries the notice fields when one is.
4. **Dashboard manual check**: with a mocked update status, the toast appears once, auto-dismisses, and respects the per-version `localStorage` dismissal.
5. **Validation commands** (filtered, single project, isolated artifacts):
   - `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --artifacts-path .bin-test --filter "FullyQualifiedName~UpdateCheck|FullyQualifiedName~UpdateController|FullyQualifiedName~CodeOrganizationConventionTests"`
   - `pnpm --prefix src/frontend/Eling.Dashboard lint` and `pnpm --prefix src/frontend/Eling.Dashboard exec tsc --noEmit`
