# Log format toggle (text `.log` ↔ JSONL `.jsonl`) — design

2026-11-05

## Intent

Eling's rolling logs are plain-text template lines. Users who want
machine-readable logs (agents, CI, downstream tooling) should be able to
switch the backend stream to JSON Lines, and back, from the dashboard.
Desktop involvement and per-process files are explicitly out of scope for
v1 (user: "config via desktop nanti aja").

## Why now

The known line-loss problem in the shared `backend.log` (verified
2026-10-22) is a *file topology* problem (many processes, one file). It is
**not** addressed here — switching to JSONL on the same shared file makes
logs parseable without fixing that pain. Keeping the two problems separate
is the point: this change must stay cheap and reversible.

## Goals

- **G1.** Per-user config `format: "text" | "jsonl"` edited on the
  dashboard; stored like `SemanticJudgeStore` / `SeqTelemetryStore`.
- **G2.** Format selects the formatter AND the active file extension:
  text → `backend.log`, jsonl → `backend.jsonl`. No mixed-format file
  ever exists; archives keep their own extension
  (`backend-2026-11-05.log` / `.jsonl`).
- **G3.** Readers (`LogReader`, MCP `LogTools`, dashboard SSE tail) work
  for both formats: `ListFiles` picks up both `*.log` and `*.jsonl`;
  level filter understands the JSONL `Level` field (full Serilog names:
  `Verbose`, `Debug`, `Information`, `Warning`, `Error`, `Fatal` —
  verified probe of `Serilog.Formatting.Json.JsonFormatter` on 4.4.0,
  which emits plain `Timestamp`/`Level` fields, not the OTel-compact
  `@t`/`@lt` names); `contains` is a raw substring over the line in
  both formats.
- **G4.** JSONL events carry structured properties (existing enrichers
  `ProcessId`, `ProjectId`, `CorrelationId`, `AspNetEnvironment` plus
  any `{Prop}` message-template properties) — free from
  `Serilog.Formatting.Json.JsonFormatter`, no new package.

## Non-goals

- Per-process log files / fixing shared-file line loss (approach B/C,
  later).
- Desktop (`desktop.log`) reading or writing the config. Desktop stays
  text; it gets a consumer later if ever.
- Converting existing archives (7-day retention means they age out).
- `contains` search inside parsed JSON properties (raw substring only).

## Anti-goals

- A mixed-format file that forces per-line detection everywhere.
- Live hot-swapping the formatter without a clean file boundary.

## Constraints

- `.NET 10, Serilog 4.4` — `JsonFormatter` is in Serilog core; zero new
  packages.
- One binary, two roles (stdio MCP host + dashboard host); config is
  shared across processes, so the store uses the
  `RefreshIfStale` pattern already used by `ToolPolicyStore`.
- Retention/pruning logic in `RollingDailyFileSink` must prune both
  extensions.

## Approaches considered

**A — Format toggle, same shared file, extension follows format.**
Cheapest correct diff. Switching format starts a *new* active file, so
rollover date-regexes never see a mixed file. Reader changes are
additive, not rewrite.

**B — Per-process files (`backend-{pid}.log`).** Fixes the 2026-10-22
line-loss pain structurally, but multiplies every reader path (tail,
SSE merge, `ListFiles`) and was never requested. Deferred.

**C — B + JSONL.** Same as B, plus. Not on the table.

**Chosen: A.** It is what the user asked for ("pindah2 antar .log dan
.jsonl", config on the dashboard); B/C stay as a follow-up that composes
cleanly on top of A's format flag.

## Design

### Config

`<user-scope>/config/log.json` (next to `semantic-judge.json`,
`tools-policy.json`, `seq.json`):

```json
{ "format": "jsonl" }
```

`LogConfigStore` mirrors `SemanticJudgeStore`: template written only
when the file is absent, atomic temp-file write, `RefreshIfStale` for
cross-process reads. Absent/corrupt/unknown value → `text` (today's
behavior).

**Types.** Internal code uses `enum LogFormat { Text, Jsonl }`. The
file DTO keeps `string? Format` (hand-editable, no JSON enum
converter); parsing is case-insensitive `Enum.TryParse` with fallback
to `Text`. Endpoint request/response serialize the enum name in
camelCase (`JsonNamingPolicy.CamelCase` is already the repo default),
so the wire shape is `"text"` / `"jsonl"` — no converter needed.

### Endpoints

`GET /api/logs/config` → `{ "format": "text" }`
`PUT /api/logs/config` → body `{ "format": "jsonl" }`, returns new view.
One field; validation is an enum check.

### When it takes effect

- **MCP hosts** (per-session): read at start; next session picks it up.
- **Dashboard host** (long-running, serves the PUT): rebuilds the
  Serilog static logger in-process, same decision as the Seq design
  ("PUT rebuilds the live provider in the process that served it").
  UI shows one line: "applies to new MCP sessions immediately after
  save; this host applies now."

Mechanism: a **`LogFormatForwardingSink`** (mirrors `SeqForwardingSink`
from the Seq design) is what gets registered in DI — a stable
`ILogEventSink` holding a nullable inner `RollingDailyFileSink`.
`DailyLogRollerService` keeps pointing at the forwarder and rolls the
current inner. On the PUT: build a new inner sink (formatter + active
file name per new format), archive the old active file, swap the
inner, `Log.Logger = config.CreateLogger()`, dispose the old sink.
The container itself never sees a changed sink.

A deliberately-unwatched corner case: an MCP host that is *currently
running a session* at switch time keeps its old format until that
session ends. No file watcher is added to fix this — the Seq design
ruled that out on the same ground, and the switch is a rare user
action, not a per-call one.

### Writer

`ElingLoggingConfig.CreateSink(logsDirectory, activeFileName, archivePrefix, format)`:

- `text` → `backend.log` / `backend`, `MessageTemplateTextFormatter(DefaultOutputTemplate)` — today.
- `jsonl` → `backend.jsonl` / `backend`, `new JsonFormatter()`.

`RollingDailyFileSink` gains one `datePrefixRegex` (or equivalent):
text files match `^\d{4}-\d{2}-\d{2}`; JSONL lines carry the date in
`"@t":"2026-11-05T..."`, so the sink matches `"@t":"\d{4}-\d{2}-\d{2}`
when the active file is `.jsonl`. Chosen at construction, never
re-evaluated per line. `PruneOldLogs` globs `{prefix}-*.log` **and**
`{prefix}-*.jsonl`.

### Reader

`LogReader`:

- `ListFiles(dir, stream)` → `*.log` **and** `*.jsonl`.
- `ResolvePath(dir, "backend", date, format)` → extension from format;
  no date + no explicit format → active file for the *current* config.
- **Level filter:** JSONL lines match on the `"Level":"Error"`-style
  field (full Serilog level names; the verified `JsonFormatter` wire
  shape uses plain `Timestamp`/`Level` fields); text lines match
  exactly as today. A file's format is known from its extension, so no
  per-line guessing.
- `contains`: raw substring over the line, both formats (anti-goal).
- SSE tail endpoint reads the active file per current config; on a
  format switch mid-stream the tail follows the new file (position
  reset is already handled on file disappearance).

### Dashboard UI

- **Format control.** The logs page gains a format section in the
  Judge/telemetry pattern: radio/segment `Text (.log)` /
  `JSON Lines (.jsonl)`, save bar, the one line about effect timing.
- **The page follows the data source automatically**: history read and
  SSE tail both resolve the active file from the current config, so
  after a switch the page shows the new file's lines with no UI state
  to migrate. Raw JSONL lines render as-is (monospace box, wrap
  toggle works on them).
- **Three JSONL-awareness patches in `Logs.tsx`** (each a few lines):
  1. `levelOf`'s bracket-token regex misses `"@lt":"Err"` → JSONL lines
     would all render "info" colour. Extend the matcher with the JSONL
     token.
  2. The date dropdown's `-(\d{4}-\d{2}-\d{2})\.log$` regex (line 243)
     hardcodes `.log` → `.jsonl` archive dates never appear as choices.
     Match both extensions.
  3. The `f.name !== \`${stream}.log\`` active-file filter (line 241)
     assumes the active file is always `.log` → exclude by the
     current format's active name instead.

### Tests

- `LogConfigStoreTests` — template when absent, atomic write, corrupt
  file or unknown value → `Text` (case-insensitive parse).
- `RollingDailyFileSinkTests` — JSONL rollover archives to
  `backend-YYYY-MM-DD.jsonl`, prunes both extensions.
- `LogReaderTests` — listing returns both extensions; level filter on a
  JSONL line matches `@lt`; `contains` works on both.
- `LogsConfigEndpointsTests` — GET/PUT round-trip.
- `Logs.tsx`: JSONL level colouring, `.jsonl` dates in the dropdown,
  active-file exclusion by format.

## Calls I made, for review

1. **Extension follows format** (your ".log vs .jsonl" phrasing) — no
   mixed files, no per-line detection. Mid-day switches are
   **non-migrating**: the old file stops being written, gets archived
   with its date so retention prunes it, and the two files for one day
   coexist harmlessly (both readable by `ListFiles`).
2. **Single `format` field, backend stream only** — desktop deferred per
   your "nanti aja".
3. **Contains = raw substring** — parsing JSON per line is 2× work for
   no user-visible gain in v1.
4. **Live rebuild on the dashboard host** — consistent with the Seq
   decision; MCP hosts apply at next start.

### Mid-day switches: no migration

Switching format mid-day does **not** migrate, convert, or merge data.
Each file simply stops receiving writes; that day's lines live split
across two files (`backend.log` up to the switch, `backend.jsonl` from
the switch). The readers cover both. Two rules keep the directory tidy:

- **Archive the stale active on switch.** The switch handler renames the
  old active file to `backend-{today}.log` (append if it already
  exists) before starting the new one, so the 7-day retention prunes it
  on schedule instead of it lingering until the user switches back.
- **Self-heal on switch-back.** When a user later returns to a format,
  the existing `TryRotateActiveLog` multi-day splitting already moves
  any stranded prior-day lines into their dated archives.

No text→JSONL conversion: it is lossy (template → properties needs
assumptions) and the 7-day retention makes it pointless.

### Console sink

Stays **text** regardless of the file format setting. The setting governs
file output only (the machine-consumed surface); the console is the
human dev surface. `WriteTo.Console(outputTemplate: DefaultOutputTemplate)`
in `McpLoggingExtensions` is not touched — the format switch re-builds
only the `RollingDailyFileSink`, so there is no second formatter to
thread through. Piping console output as JSON is a niche capture case,
not on the table.

## Left to the builder

- Exact dashboard page placement (logs settings section vs its own nav
  entry) — follow whatever the logs UI already looks like.
