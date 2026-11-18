# Log MCP tools read JSONL — short design

2026-11-11

## Intent and why

The MCP `log_read` tool ignores the user-scope `log.json` format setting
and always resolves the **text** log. With `format: "jsonl"` configured
(the user's current setting), agents calling `log_read` get `backend.log`,
which stopped growing on 2026-11-07, instead of the live `backend.jsonl` —
silently stale diagnostics. The dashboard REST endpoints already resolve
the extension from `LogConfigStore`; the MCP path does not. This makes the
MCP tools behave the same way.

## Goals

- `log_read` reads the file(s) for the **currently configured format**
  (`log.json`, via `LogConfigStore.GetFormat()`, which re-checks staleness
  per call — same semantics as `/api/logs/read`) **and spans both formats**
  for the requested date/today (see "Cross-format reads" below).
- Optional `format` parameter on `log_read`: `text` | `jsonl` | `auto`
  (default `auto` = config). Explicit garbage → `invalid_argument`.
- Lines are returned **raw** — a JSONL line is one JSON object string,
  exactly as written. No parsing, no pretty-printing (user decision
  2026-11-11).
- The response carries a `format` field so the caller can see which kind
  of file it read.
- `log_list` unchanged (it already lists `*.log` and `*.jsonl`); its
  description is touched up to say so.
- The agent-loop adapter (`LogReadAgentTool`) gains the same optional
  `format` parameter in its schema and passes it through.

## Non-goals

- No parsing or field-level filtering of JSONL (raw substring `contains`
  stays, per the 2026-11-05 spec's anti-goal).
- No change to the dashboard, the sink, or `RollingDailyFileSink`.
- No `format` parameter on `log_list`.
- No migration of archives.

## Cross-format reads (amendment 2026-11-11, user-directed)

A mid-day format switch leaves **two archives for the same date**
(`backend-2026-11-06.log` + `backend-2026-11-06.jsonl`, both present in
the user's logs directory). Reading only the configured extension loses
half the day. A read therefore gathers every segment it can, in order:

- **Dated read** (`date=YYYY-MM-DD`): `{stream}-{date}.log` and
  `{stream}-{date}.jsonl` — whichever exists (zero, one, or both).
- **Active read** (no date): today's `{stream}-{today}.log` and
  `{stream}-{today}.jsonl` archives (a switch today parks the stale
  active as an archive) plus the active `{stream}.{ext}` for the current
  format. The active file is the newest segment.

Segments are merged, filtered, and tailed as **one sequence**:

1. Extract a sort key per line: text lines use the leading
   `yyyy-MM-dd HH:mm:ss.fff zzz`; JSONL lines use the `"Timestamp":"..."`
   member. `DateTimeOffset.TryParse` (InvariantCulture) both.
2. Stable merge/sort by that key; lines whose key fails to parse keep
   their segment order (text segment first, then jsonl — the write order
   on a switch day).
3. `totalLines`/`filteredTotal` are summed across segments; `tail`
   applies to the merged result, so "last 100 matching lines today" is
   correct even across a switch.
4. The per-file byte budget applies to the window of kept lines only
   (oldest matched lines are dropped until the kept tail fits
   `MaxResponseBytes`), never by stopping the scan; the merged selection
   is re-capped to `MaxResponseBytes` (oldest dropped). The response's
   `tail` bound is unchanged; totals are counted over the whole file.
5. The response reports `files` (all files actually read, in segment
   order) alongside the legacy `file` (first segment) for compatibility.

This covers double-switch days (text → jsonl → text) without special
casing: the per-line sort key reorders correctly regardless of how many
segments the day has.

## Approaches considered

1. **`Func<LogFormat>` seam, store registered per host (chosen).**
   `LogTools` takes `Func<LogFormat>? getFormat = null` (null → `Text`,
   today's behavior, keeps unit tests green and tests the JSONL path by
   passing `() => LogFormat.Jsonl`). The dashboard host resolves it via
   `AddElingAgentTools` from its existing `LogConfigStore` singleton (falling
   back to `Text` when the store isn't registered). The MCP stdio host
   registers `LogConfigStore` + the `Func<LogFormat>` in `McpHostBuilder`.
   Smallest diff, no new abstraction beyond one delegate, trivially testable.
2. **`LogConfigStore` injected directly into `LogTools`.** Rejected:
   `LogTools` is created by `WithToolsFromAssembly()` via
   ActivatorUtilities; a required store parameter forces registration in
   every host that auto-discovers the tool, and a `Func<LogFormat>` is the
   narrower seam the tool actually needs.
3. **Read `log.json` inside `LogTools` with a private parser.** Rejected:
   duplicates the store's caching/staleness logic and its degrade-to-Text
   rules, and breaks in tests the same way (real user profile read).

## Design

### `LogTools` changes

```csharp
public LogTools(string? logsDirectory = null, ILogger<LogTools>? logger = null,
                Func<LogFormat>? getFormat = null)
```

`ReadLog` gains:

```csharp
[Description("Optional format override: 'text' or 'jsonl'. Empty or 'auto' uses the configured format (log.json).")]
string? format = null
```

Resolution: `format` null/whitespace/`auto` → `getFormat?.Invoke() ?? LogFormat.Text`;
`text`/`jsonl` (case-insensitive) → that format; anything else →
`invalid_argument` error JSON. The resolved format no longer selects a
single file — see "Cross-format reads" (it only orders the line sort) — and
echoed in the response as `"format": "jsonl"`, plus `files` (all files
read, in segment order) keeping `file` as the first segment for
compatibility. The dated-archive fallback inside `LogReader.Read` is
replaced by the multi-segment merge described above.

`ListLogs` description update only.

### Wiring

- `AddElingAgentTools` (used by the dashboard host): `TryAddSingleton<Func<LogFormat>>`
  resolving `sp.GetService<LogConfigStore>()` → its `GetFormat()`, else
  `() => LogFormat.Text`. No new required services; bare test containers
  keep `Text`.
- `McpHostBuilder` (MCP stdio host): register `LogConfigStore`
  (`shared.UserScope`) and the same `Func<LogFormat>` singleton. The host
  otherwise auto-discovers `LogTools` with `getFormat` resolved from DI.
- `LogReadAgentTool`: schema gains `format` (optional string, same
  semantics); description notes the default follows `log.json` and that
  JSONL lines are raw JSON object strings.

### Tests

- `LogToolsTests`: JSONL read via `getFormat: () => LogFormat.Jsonl` on a
  temp dir with `backend.jsonl`; explicit `format: "jsonl"` overrides a
  Text config and vice versa; invalid `format` → `invalid_argument`; no
  `getFormat` → `Text` (existing tests unchanged); response carries
  `format` and `files`.
- `LogReaderMergeTests.cs`: dated read with **both**
  `backend-D.log` and `backend-D.jsonl` present → merged, timestamp-
  ordered lines; tail applies across the merge; `contains` matches lines
  from both formats (a text line and a JSONL line); sums of
  `totalLines`/`filteredTotal`; active read merges today's archives +
  the active file; a JSONL line with an unparseable timestamp keeps
  segment order.
- `AgentBuiltinMcpToolTests`: unchanged (21 tools, `Text` fallback).
- No frontend changes.

### Rollout note

This changes the repo, not the running binaries. The MCP servers and the
installed dashboard pick the fix up on the next backend rebuild/publish;
until then `tools.eling.log_read` keeps the stale behavior.

## Calls I made, for review

1. `format` on `log_read` only, not `log_list` (it is format-agnostic).
2. Garbage `format` is an error, not a silent fallback — silent fallbacks
   in agent tools hide misconfiguration.
3. `Func<LogFormat>` rather than injecting `LogConfigStore` — narrower
   seam, keeps ActivatorUtilities discovery working, tests need no store.
4. `auto` accepted as an explicit spelling of the default, since agents
   tend to pass something when they see the parameter.
5. Cross-format reads merge **today's archives too** (not only dated
   reads): a mid-day switch splits "today" across two files, and an
   agent asking for today's logs should see both halves (user-directed
   2026-11-11).
6. Sort key parse failure → keep segment order (text first, then jsonl),
   never drop or throw — a hole in the file must not break the merge.
