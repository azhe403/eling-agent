# Eling Self-Update Tool — Specification

**Date:** 2026-10-22
**Status:** Draft (Awaiting User Review)
**Scope:** `Eling.Backend` (new MCP tool + swap engine), `docs/agent-setup.md` (manual update path), `docs/superpowers/specs/2026-10-09-update-check-design.md` (cross-reference), test suites

---

## 1. Overview & Goals

Eling ships as a binary that a user installs once and then never updates. The
update-check feature (see `2026-10-09-update-check-design.md`) detects that a
newer release exists and *notifies* — through a dashboard snackbar and through
an `update` block riding on the `memory_recall` response. Nothing ever applies
the update. The user is told to run a one-line installer by hand.

That hand-off breaks in exactly the situation where the user most wants help:
the agent is the one being asked to update Eling, and on Windows the running
`eling-backend.exe` locks its own file. The naive "run the installer" step
fails with a sharing violation, and the documented fallback is to ask the user
to close the desktop client by hand.

This specification adds one MCP tool, `self_update`, that applies the update
from inside the running Eling process: the agent calls it after the user says
"update eling", and the update is staged, verified, and swapped into place
without the user touching a terminal or closing anything.

Goals:

1. **One call, no ceremony.** The user says "update eling" in chat; the agent
   calls `self_update`; the new binary is in place for the next start.
2. **No process killing, no fiddling.** The update itself must not require
   `taskkill`, closing the desktop client, or any other manual step. The
   one thing it does require is that Eling restarts so the new binary runs,
   and that restart is offered as an explicit, user-consented act (§4.6).
3. **Verified bytes.** The downloaded asset is checked against the SHA-256
   digest GitHub publishes for the asset before anything is swapped in.
4. **Honest reporting.** The tool states what it did, what it did not do, and
   what the user must still do (restart). It never reports success for a
   partial update.
5. **Consent where it costs something.** Ending the Eling process takes the
   agent's memory tools offline for a moment, so the tool never does it on
   its own initiative.

Non-goals:

- Updating the **desktop app** (`eling.exe`). It is a separate release asset
  with a separate install path, and is explicitly out of scope.
- **Silent or scheduled auto-update.** The tool only runs when an agent calls
  it, which happens only after the user asked for an update.
- **Downgrades.** A release that is not strictly newer than the running binary
  is never installed.
- **Data migration.** Memories (`.eling/memories/*.md`) and the rebuildable
  `memory.db` cache are untouched by an update.
- **Updating a dev build.** A binary running from a source checkout is not an
  installed release; the tool refuses and says so.

---

## 2. Surfaces and naming

The capability ships as an **MCP tool on the Eling backend**, not as a skill,
slash command, or shell script.

Rationale: the contract (which asset to fetch, what to verify, how to swap,
what to report) belongs next to the code that implements it, and it must work
identically in every host that can talk MCP — OpenCode, Claude Code, Claude
Desktop, Cursor, Antigravity, Codex. A skill would need installing on every
machine; a slash command would only work in one host. A tool in the Eling
server works everywhere the server is already registered.

**Tool name:** `self_update`.

Namespaced, as every host renders it:

| Host | Rendered name |
|---|---|
| Claude Code | `mcp__eling__self_update` |
| OpenCode | `eling.self_update` (dev server: `eling_dev.self_update`) |

The subject is deliberately not `eling`: the server already carries that name,
and `mcp__eling__eling_update` stutters. `self` has no other referent — inside
the `eling` namespace it can only mean Eling itself. `system` was rejected
because `api/system` already denotes the *host machine* in this repository
(`SystemController`), and "system update" universally connotes an operating
system update. `backend` was rejected because it names the artifact rather
than the target and reads like "update the backend of the user's project".

**Tool group:** `eling` — a new group in `ToolCatalog`, so the tool can be
toggled by the existing group-based tool policy and shows on its own in the
dashboard's tools page. It is not a protected tool (the protected set stays
`tools_policy` and `memory_recall`).

---

## 3. Tool contract

```csharp
[McpServerTool(Name = "self_update")]
public Task<SelfUpdateResult> UpdateAsync(
    [Description("When true, only report what an update would do; download nothing.")]
    bool dryRun = false,
    [Description("When true, end this Eling process a few seconds after the call so the host starts the new binary. Only pass this after the user has explicitly confirmed the restart. The eling tools disappear until the host reconnects the server.")]
    bool restart = false);
```

`dry_run` defaults to `false`: the tool's whole purpose is to apply the
update, and the user's "update eling" already is the consent. `dry_run: true`
exists for "is there anything new?" without touching the disk.

`restart` defaults to `false`, and that default is deliberate. Restarting
Eling is a separate act with its own cost: it ends the process the agent is
currently talking to, so the eling tools go dark until the host reconnects
them. The tool therefore never restarts anything on its own — an agent may
only pass `restart: true` after the user has explicitly said yes to the
restart. The tool's own description carries that rule, because that is where
an agent reads the contract. See §4.6 for what a restart actually does.

### 3.1 Result shape

```json
{
  "status": "up-to-date | planned | staged | applied | restarting | failed | unsupported",
  "currentVersion": "0.1.0-pre.46",
  "installedVersion": "0.1.0-pre.47",
  "latestVersion": "v0.1.0-pre.47",
  "updateAvailable": true,
  "restartRequired": true,
  "releaseUrl": "https://github.com/azhe403/eling-agent/releases/tag/v0.1.0-pre.47",
  "assetName": "eling-backend-win-x64.zip",
  "installDirectory": "/home/user/.local/bin",
  "appliedAt": "2026-10-22T04:12:33.0000000Z",
  "message": "Eling v0.1.0-pre.47 installed. The running process still executes v0.1.0-pre.46 until Eling restarts.",
  "nextStep": "Restart the agent host (or reconnect the eling MCP server) to run the new binary."
}
```

`currentVersion` is the version of the code that is executing right now.
`installedVersion` is the version of the binary on disk — after a swap these
two differ, and the difference is exactly what "restart required" means.
`restartRequired` is true whenever the binary on disk is newer than the
running code.

| Status | Meaning |
|---|---|
| `up-to-date` | The binary on disk is already the newest release. Nothing was downloaded. |
| `planned` | A newer release exists; `dry_run` was set, so nothing was downloaded. |
| `staged` | The new binary is verified and staged, but the swap could not be completed. It is applied at the next startup. |
| `applied` | The new binary is in place. The running process still executes the old image until Eling restarts. |
| `restarting` | The update is applied and this process will exit shortly, so the host starts the new binary. |
| `failed` | The update did not happen. `message` says why. |
| `unsupported` | The newest release has no asset for this platform, or the running binary is not an installed release. |

### 3.2 Agent-facing description

The `[Description]` on the tool states, in the tool contract itself: call this
only when the user asks for an update; it replaces the Eling backend binary in
place; it does not update the desktop app; pass `restart: true` only after
the user has explicitly confirmed the restart, because it ends the Eling
process and the host has to reconnect it; report the result to the user and
tell them to restart the host to run the new version. This is the only place
an agent needs to look, which is the point of shipping it as a tool rather
than as documentation.

---

## 4. How the update is applied

### 4.1 The locked-binary problem

On Windows a running image cannot be deleted or overwritten, only renamed. The
running `eling-backend.exe` is, in the common case, the very file that must be
replaced — and it may be running twice on the same machine (the agent's MCP
stdio child and the dashboard owner on port 4317), both holding the same file.

The design leans on the one Windows behaviour that makes this possible:

> A running executable may be **renamed**, but not deleted or overwritten.

So the swap never overwrites a running file. It moves the running file out of
the way, then moves the new file into its place. Both holders keep running
from the renamed image; the next start of `eling-backend.exe` runs the new
code.

### 4.2 Sequence

```
tool call
  │
  ├─ 1. resolve current version      (InformationalVersion of the running assembly)
  ├─ 2. fetch newest non-draft release (GitHub API, existing GitHubReleaseClient)
  ├─ 3. compare                      (existing SemanticVersionCompare.IsNewer)
  │     not newer            → status=up-to-date, stop (no download happens)
  ├─ 4. pick the asset for this platform → missing → status=unsupported
  ├─ 5. download to a temp file
  ├─ 6. verify SHA-256 against the release asset's published digest
  │     mismatch            → status=failed, delete the download
  ├─ 7. extract into <installDir>/.eling-update/<tag>/
  │     missing binary or missing dashboard-ui/index.html → status=failed
  ├─ 8. swap  (UI folder first, binary last — see §4.4)
  ├─ 9. write <user-scope>/config/last-update.json
  └─10. return status=applied with the restart instruction
```

Step 9 is what lets the next session report what happened: the dashboard and
`GET api/update/status` show "updated from v0.1.0-pre.46 to v0.1.0-pre.47 at
…", so the user can confirm the update landed instead of guessing.

### 4.3 The swap

The install directory is `AppContext.BaseDirectory` — the directory the running
binary actually lives in. For an installed backend that is `~/.local/bin`
(`%USERPROFILE%\.local\bin` on Windows), the same place the installer writes.

```
staging/eling-backend.exe          install/eling-backend.exe.old-<pid>   (rename)
staging/eling-dashboard-ui/        install/eling-dashboard-ui.old-<pid>  (move aside)
                                   install/eling-dashboard-ui/          (move in)
                                   install/eling-backend.exe           (move in)
```

Order matters. The dashboard UI is not locked, so it is replaced first; the
binary is renamed into place last. If the binary step fails, the old file is
renamed back and the result is `staged` (the new files are still on disk, and
the next startup applies them) rather than a half-updated install.

The `.old-<pid>` files cannot be deleted while a holder still has them open.
They are removed by the next successful startup, which sweeps `*.old-*` and
`*.pending-update` from the install directory.

**Fallback when the rename is refused.** Some scanners hold a file open in a
way that blocks renaming too. When the rename fails, the staged binary is
written as `install/eling-backend.exe.pending-update` and the result is
`staged`. The next startup of Eling applies a pending update before it
serves anything, then removes the marker. This is the same mechanism the
cleanup sweep uses, so there is one code path, not two.

### 4.4 Platform behaviour

| Platform | Replace a running binary | Approach |
|---|---|---|
| Windows | Rename only | Rename the running file aside, move the new one in. |
| Linux / macOS | Unrestricted | `rename()` over the running file is fine; the running process keeps the old inode. The same rename-aside path is used anyway, so one implementation covers both. |

The implementation uses the same rename-aside sequence on every platform. It
is not the shortest path on POSIX, but it is one code path, and one code path
is one thing to test.

### 4.5 Concurrency and safety

- A lock file (`<installDir>/.eling-update.lock`) is taken for the duration of
  the swap. A second concurrent `self_update` call reports the in-progress
  state instead of racing.
- Free disk space is checked before the download; the asset is roughly 50 MB
  compressed and the extracted payload is larger.
- A failed or abandoned staging directory is removed on the next start.
- The tool never touches anything outside the install directory and its own
  staging area.

### 4.6 Restarting after the update

What has to restart, precisely:

- The **Eling MCP process** — the agent's own memory server — is the only
  process that must restart. It is the process whose code was replaced.
- The **agent host** does not have to restart for the update to take effect.
  It only has to restart if it chooses not to reconnect a dead MCP server.
- The **dashboard instance** (the process owning port 4317) is a separate
  process running the same binary. It keeps executing the old image until it
  restarts too. The tool reports this rather than ending it: it may be
  serving a browser the user is looking at, and it is a different process
  from the one the user asked about.

The restart itself is a graceful shutdown of the MCP host
(`IHostApplicationLifetime.StopApplication()`), scheduled a few seconds after
the tool call returns, with a watchdog that forces the exit if the graceful
path hangs. The delay exists so the JSON-RPC response reaches the client
before the process disappears: a call that dies mid-flight reads as a
failure, and the agent would then report a failed update for an update that
actually succeeded. The request goes through a small interface
(`IProcessExitRequest`) so tests can assert it without ending the test
process.

**The honest part — hosts do not respawn a dead MCP server.** OpenCode's MCP
implementation marks a closed server entry as `failed` and stops it; the
automatic recovery path is reserved for an expired HTTP session, not for a
process that exited (`packages/core/src/mcp/index.ts`: `onClose` → `failed` →
`stopServer`, while `recover` is wired only to `onSessionExpired`). Claude
Code behaves the same way — a server that died shows as failed and has to be
reconnected by hand.

So `restart: true` is best-effort by nature, and the three outcomes differ:

- The user restarts the host → guaranteed, and the new binary runs.
- The user reconnects the eling server from the host's MCP panel → the new
  binary runs.
- The agent ends the process on the user's confirmation → the host may leave
  the server in a failed state, and the agent has no programmatic way to
  reconnect it. The eling tools stay dark until the user acts.

That asymmetry is exactly why `restart` defaults to `false`, and why the
tool's message names the host restart as the reliable path. The tool's job
is to make the *update* painless; the *restart* is a host concern that only
the user can settle. The agent may offer it, but never assume it.

---

## 5. Version and asset selection

### 5.1 Platform → asset

The asset name is derived from the operating system and the process
architecture, not from `RuntimeInformation.RuntimeIdentifier`: the RID string
is framework-dependent and may read `win10-x64`, which is not an asset name.

| OS | Architecture | Asset |
|---|---|---|
| Windows | x64 | `eling-backend-win-x64.zip` |
| Windows | arm64 | `eling-backend-win-arm64.zip` (only if the release publishes it) |
| Linux | x64 | `eling-backend-linux-x64.tar.gz` |
| Linux | arm64 | `eling-backend-linux-arm64.tar.gz` |
| macOS | x64 | `eling-backend-osx-x64.tar.gz` |
| macOS | arm64 | `eling-backend-osx-arm64.tar.gz` |

The release currently publishes five RIDs: `win-x64`, `linux-x64`,
`linux-arm64`, `osx-x64`, `osx-arm64`. A platform with no published asset
yields `unsupported` — the tool does not cross-build.

### 5.2 Integrity

GitHub publishes a `digest` field per release asset (`sha256:<hex>`). The
downloaded file's SHA-256 is compared against it. A mismatch is a hard
failure: the download is deleted and the result is `failed`.

If a release predates asset digests and publishes none, the tool proceeds and
logs a warning — the download still comes from the official release over
HTTPS, and refusing would strand users on an old release for no security gain
that the transport does not already provide.

### 5.3 The version gate

`SemanticVersionCompare.IsNewer` (existing, tested) decides whether anything
happens. A release that is not strictly newer is never installed, so the tool
cannot downgrade.

The comparison is against the version **on disk**, not the version running.
Those two differ exactly in the window between a swap and a restart, and
comparing against the running version would make the tool download the same
release again on every call until the user restarts. The installed version
comes from the binary's version resource, with the `last-update.json` marker
as the fallback for binaries this tool installed itself. When neither can be
resolved — a binary built without version stamping — the tool reports
`failed` with an explanation instead of guessing.

The same marker keeps the update-check quiet in that window: after a swap,
the running process still reports the old version, so the periodic check
would otherwise nag that an update is available seconds after one was
applied. The marker says what is on disk, and the check compares against
that. One marker, two consumers.

### 5.4 The "not an installed release" guard

The tool updates the directory the running binary lives in. When that
directory is a source checkout (a dev build running from `.bin/`), the tool
refuses with `unsupported` and says so: there is no installed binary there to
replace, and the user wants the *installed* one updated.

---

## 6. Reporting the applied update

`<user-scope>/config/last-update.json` records the outcome:

```json
{
  "from_version": "0.1.0-pre.46",
  "to_version": "0.1.0-pre.47",
  "installed_version": "0.1.0-pre.47",
  "applied_at": "2026-10-22T04:12:33.0000000Z",
  "status": "applied"
}
```

`installed_version` is what is on disk, which is what the version gate and
the update-check compare against. `status` distinguishes an applied swap from
a staged one that never landed.

`GET api/update/status` gains the last applied update alongside the current
check status, so the dashboard can show it. The file follows the existing
config conventions: safe write (temp file, then move), timestamps generated in
backend code, and a corrupt file degrading to "unknown" rather than throwing.

---

## 7. Bootstrap: the first update on a machine

The tool exists only in binaries that contain it. A machine running an older
release updates once by hand — the one-line installer — and every update after
that is a single tool call.

This is stated in `docs/agent-setup.md` (a new "Update" section) and in the
tool's own description, so an agent on an old binary still knows the manual
path. The first release carrying `self_update` is therefore the release that
makes the workflow universal; there is no way to bootstrap a tool into a
binary that predates it, and pretending otherwise would mean shipping a
bootstrapper of its own.

The desktop app keeps its own update story; the tool does not extend to it.

---

## 8. Relationship to the existing update-check design

`2026-10-09-update-check-design.md` owns **detection** — the periodic poll,
the cache, the semver comparison, the notice in the `memory_recall` response.
This specification owns **application** — the tool that acts on what detection
found.

That document's §5.2 states there is deliberately no `check_update` MCP tool.
That remains true: detection still rides on `memory_recall` and on
`api/update/status`, adding no tool call to session start. What changes is
that the *action* now has a tool, so an agent no longer has to hand the user a
shell command to paste. The old document's §5.3 (the agent runs the OS
installer on approval, with the "never kill a process" rule) is superseded by
this design: the tool performs the update itself and never needs to kill
anything. The old document gets a pointer to this one rather than a rewrite.

---

## 9. Verification plan

Tests live in `tests/Eling.Backend.Tests/Updates/`, following the existing
conventions in that folder (xUnit, one behaviour per test, no tuples).

1. **Platform mapping** — each supported OS/architecture pair maps to the
   right asset name; an unpublished platform yields no asset.
2. **Version gate** — a not-newer release performs no download; a newer one
   does. The HTTP client is faked, so this is observable.
3. **Installed versus running** — after a swap, a second call reports
   `up-to-date` with `restartRequired: true` and downloads nothing. The
   comparison is against what is on disk, not against the running code.
4. **Digest verification** — a matching digest proceeds; a mismatching digest
   fails and leaves no staged files behind.
5. **Staging layout** — a staged payload missing `eling-dashboard-ui/index.html`
   is rejected; a complete one is accepted.
6. **Swap** — the binary is renamed aside and the new one takes its place; the
   old file is preserved under the `.old-<pid>` name; a failing binary step
   restores the old name and reports `staged`.
7. **Pending-update fallback** — a pending marker present at startup is
   applied and cleared.
8. **Cleanup sweep** — stale `.old-*` and `.pending-update` files are removed
   on startup, and abandoned staging directories are removed.
9. **Tool result** — each status produces the documented result shape; a
   `dry_run` performs no download and no filesystem write.
10. **Restart request** — `restart: true` asks the exit interface to end the
    process; `restart: false` does not. The test never ends a real process:
    the interface is faked.
11. **Update-check quiet** — with the marker reporting a newer installed
    version, the periodic check does not report an update as available.
12. **Regression** — the existing update-check tests, the update controller
    tests, and the code-organization convention tests still pass.

Manual verification: on a machine with an installed release, call the tool
against a real pre-release, confirm the swap, restart the host, and confirm
`GET api/update/status` reports the applied version.

---

## 10. Risks and mitigations

| Risk | Mitigation |
|---|---|
| Windows refuses the rename (scanner holding the file) | Fall back to a pending-update marker applied at the next startup; report `staged` rather than failing silently. |
| Partial swap leaves an inconsistent install | The UI is replaced before the binary; a failed binary step restores the previous file name and reports `staged`, never `applied`. |
| Two updates race | A lock file in the install directory; the second call reports the in-progress state. |
| The new binary is broken | The release asset is the same artifact CI publishes and the installer already installs; a bad release breaks the installer path too. The `last-update.json` marker identifies exactly what was applied. |
| Disk space | Checked before the download; the failure message says so. |
| A dev build tries to update itself | Refused with `unsupported` (§5.4). |
| The host does not respawn a dead MCP server | The restart is opt-in and the message names the host restart as the reliable path (§4.6). The tool never ends a process the user did not confirm. |
| The update-check nags right after an update | The `last-update.json` marker reports what is on disk, and the check compares against that (§5.3). |

---

## 11. Decisions taken in this document

These were settled while designing and are recorded here so a reviewer can
check them rather than discover them:

- The capability is an MCP tool, not a skill or a command.
- The tool is named `self_update`, in a new `eling` tool group.
- `dry_run` defaults to `false`.
- The swap is rename-aside, on every platform, with a pending-update
  fallback. No helper process, no `--apply-staged-update` CLI mode, no
  `Process.Start` — which also means no static-dependency seam is needed for
  testability.
- The install directory is `AppContext.BaseDirectory`; the desktop app is out
  of scope.
- Digest verification is mandatory when the release publishes a digest, a
  warning when it does not.
- The applied update is recorded in `last-update.json` and surfaced through
  `api/update/status`.
- The first update on a machine predating the tool is manual; this is
  documented, not worked around.
- The tool carries a second parameter, `restart`, default `false`. Restarting
  Eling is a separate, user-consented act: the tool never ends a process on
  its own, and the agent may only pass `restart: true` after the user has
  confirmed it. The restart is a graceful `StopApplication()` scheduled after
  the response, not a kill.
- Only the Eling MCP process is ever restarted. The dashboard instance and
  the agent host are reported, never killed.
- The version gate compares against the version on disk, so a swap is not
  repeated on every call, and the update-check stays quiet in the window
  between a swap and a restart.
