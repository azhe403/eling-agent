# Seq (Datalust) telemetry — design

2026-10-27

## Intent

Eling sends its logs, metrics, and traces to Seq, and the whole thing is
configured from the dashboard. Today telemetry dies on the machine: tool
timings are written as Serilog lines into a rolling local file, runtime CPU
and memory only live inside the process that sampled them, and nothing is
visible from a second machine.

## Why now

`ToolTelemetryFilter` already measures everything worth measuring — duration,
status, request and response size, error code — but as log text, so it can
only be searched, never aggregated into a chart. `RuntimeMetricsSampler` +
`RuntimeMetricsBroadcaster` already probe every live Eling process, but the
numbers are broadcast over SSE and dropped; there is no history, and sampling
stops entirely when no dashboard tab is open. The measuring is done; only the
shipping is missing.

## Goals

- **G1 Logs.** Every Eling process ships its Serilog events to Seq when the
  user enables it.
- **G2 Metrics.** Native OTLP metrics for MCP tool calls, tool duration, tool
  payload size, and per-process CPU and memory.
- **G3 Traces.** One span per MCP tool call, carrying the same correlation id
  that is already pushed into `LogContext`, so a Seq user can jump between a
  slow span and its log lines.
- **G4 Configuration.** Per-user config file plus a dashboard page, following
  the Judge pattern: the API key is never sent back to the browser.
- **G5 Safety.** Off by default. An unreachable or misconfigured Seq never
  breaks an Eling process and never grows memory without bound.

## Non-goals

- Replacing the local rolling log files. They stay; Seq is additive, because
  a local file is still the only thing that works with no network.
- Installing, bundling, or provisioning a Seq server.
- Charting Seq data inside the Eling dashboard. Seq's own UI is the analysis
  surface.
- Per-project Seq configuration. Telemetry is a machine-level concern, so the
  config is per-user.
- Instrumenting the dashboard's own HTTP endpoints in v1.

## Anti-goals

- Eling failing to start because Seq is down, the URL is wrong, or the JSON
  is corrupt.
- An API key landing in a committed `.eling` directory, or being echoed back
  to the dashboard.
- An unbounded queue if Seq stops accepting data.

## Constraints

- Seq's metrics ingestion is new-generation (2026.1 preview builds). Logs
  ingestion over OTLP has been stable since 2023.2; the Serilog sink works
  against every version. A user on an older Seq must still get logs.
- Seq accepts metrics and traces over gRPC or HTTP/protobuf only. HTTP/JSON is
  not implemented.
- Seq's metrics reader needs **delta** temporality. Cumulative numbers show up
  wrong.
- This repo has no central package management; versions are pinned per csproj.
- .NET 10, Serilog 4.4 already referenced in `Eling.Backend.csproj`.
- One binary serves two roles: the stdio MCP host and the HTTP/dashboard host.
- `MeterProvider` and `TracerProvider` cannot be reconfigured once built.

## Approaches considered

**A — logs only.** Add `Serilog.Sinks.Seq` and a settings page. Smallest
diff, works on every Seq version, but gives no counters and no history, and
the runtime CPU/memory numbers stay invisible.

**B — metrics only.** Instrument meters and ship OTLP. Gives charts, but the
moment something looks wrong you cannot see why, because the log lines that
explain it are still local.

**C — logs plus metrics.** One config page, two toggles, both signals.

**D — C plus traces.** Adds an `ActivitySource` around the tool-call filter,
which is where the correlation id already lives. Traces have been stable in
Seq since 2024.1, so they carry less version risk than metrics do.

**Chosen: D.** The user asked to maximise what Seq does, and C is really
D minus one instrumented method — the trace source is the cheapest of the
three signals because `ToolTelemetryFilter` is already the single place every
tool call passes through. Logs tell you *what* happened, metrics tell you
*when* it got worse, traces tell you *where* the time went; Seq's value is
precisely that all three sit in one query surface.

## Design

### Configuration

`<user-scope>/config/seq.json`, next to `semantic-judge.json` and
`tools-policy.json`:

```json
{
  "enabled": false,
  "serverUrl": "",
  "apiKey": "",
  "sendLogs": true,
  "sendMetrics": true,
  "sendTraces": true,
  "minimumLevel": "Information"
}
```

One `enabled` flag plus a flag per signal, rather than three independent
configs: it is one server and one key, and the per-signal flags are what let
a user on an older Seq keep logs while switching off metrics it cannot
ingest.

`SeqTelemetryStore` mirrors `SemanticJudgeStore` exactly — private gate,
template written only when the file is absent, atomic temp-file write, empty
`apiKey` means "leave the stored key alone" so the form round-trips without
the secret. `RefreshIfStale` is copied from `ToolPolicyStore`, because the
dashboard host and every MCP host share this one file across processes.

The view DTO exposes `hasApiKey` instead of the key, and
`isConfigured = enabled && serverUrl is not empty`.

### Endpoints

- `GET /api/telemetry/seq/config` — view.
- `PUT /api/telemetry/seq/config` — merge update, returns the new view.
- `POST /api/telemetry/seq/test` — probes the configured (or supplied) server
  with `GET {serverUrl}/api` and `X-Seq-ApiKey`, and returns `ok`, `message`,
  and the detected `serverVersion` when the response parses. The dashboard
  uses the version to warn, not to block: an unparseable response reports
  `unknown` and saving still works.

### When config takes effect

The `PUT` rebuilds the live providers in the process that served it — the
dashboard host, which is the process showing the page. Every other Eling
process (each stdio MCP host) reads the file at start and picks up the change
on its next start. The page says so in one line. The alternative, a file
watcher in every process rebuilding its logger on change, buys little: MCP
hosts are per-session, so convergence is naturally fast, and a rebuild racing
an in-flight log write is a new class of bug for no user-visible win.

`SeqTelemetryRuntime` is the single owner of that lifecycle: `Apply(config)`
disposes the current `MeterProvider`, `TracerProvider`, and inner Seq sink,
then builds new ones when the config is usable. It is a singleton, called
once at startup from a hosted service and again on every `PUT`.

### Logs

`Serilog.Sinks.Seq` added to the chain through a forwarding sink rather than
directly in `AddElingLogging`, because Serilog is configured during host build
while the config is editable afterwards:

- `SeqForwardingSink : ILogEventSink` holds a nullable inner `ILogEventSink`
  and forwards to it when present. It is registered unconditionally.
- `SeqTelemetryRuntime.Apply` swaps the inner sink for a `SeqSink` pointed at
  `{serverUrl}`, or clears it.

`minimumLevel` maps to a `LoggingLevelSwitch` on that sink only, so the local
file keeps its current verbosity.

### Metrics

Meter `Eling.Backend`, exported every 15 seconds with
`MetricReaderTemporalityPreference.Delta` over
`OtlpExportProtocol.HttpProtobuf` to `{serverUrl}/ingest/otlp/v1/metrics`
with header `X-Seq-ApiKey`.

| Instrument | Type | Tags |
|---|---|---|
| `eling.tool.calls` | counter | `tool`, `status` |
| `eling.tool.duration` | histogram (ms) | `tool` |
| `eling.tool.payload` | histogram (bytes) | `tool`, `direction` |
| `eling.runtime.memory_bytes` | observable gauge | `pid`, `project` |
| `eling.runtime.cpu_percent` | observable gauge | `pid`, `project` |

Resource: `service.name = eling-backend`, `service.instance.id = pid`,
`eling.role = dashboard | mcp`, `eling.project` from the existing `ProjectId`.

The two runtime gauges are published **only by the process that owns the
dashboard port**. `RuntimeMetricsSampler.SampleAll()` already enumerates every
live runtime from the shared registry, so if each MCP host published them too,
every process would report every other process and Seq would double-count.
The role tag makes the ownership visible in Seq rather than implicit.

### Traces

`ActivitySource` `Eling.Backend.Tool`. `ToolTelemetryFilter` already sits on
the `CallTool` pipeline, so the span is opened there: name = tool name, tags
`mcp.tool`, `mcp.rpc_id`, `mcp.status`, `mcp.error_code`,
`mcp.request_size`, `mcp.response_size`, and `correlation_id` matching the
`LogContext` property it pushes today. `Activity.Current` supplies the parent
when the MCP SDK provides one.

### Failure behaviour

- Unusable config (empty URL, unparseable file) builds nothing; the process
  behaves exactly as it does today.
- Exporter and sink failures are logged at Warning, throttled, and swallowed.
- The Serilog Seq sink uses its own bounded buffer with a small limit; the
  local file sink stays the durable record. OTLP exporters keep their default
  bounded batch and drop when the backend is unreachable.

### Dashboard

`/dashboard/telemetry`, sidebar entry "Telemetry" (`Activity` icon), built on
`Judge.tsx`'s structure: enable switch, server URL, API key field that shows
`hasApiKey` as a placeholder, three signal switches, a Test connection button
that reports the detected Seq version, and a save bar. When the probe reports
a version older than 2026.1, the metrics and traces switches show an inline
hint instead of being disabled — the user may be pointing at a server that
will be upgraded.

## Files

Backend, new:

- `src/backend/Eling.Backend/Telemetry/SeqTelemetryConfig.cs`
- `src/backend/Eling.Backend/Telemetry/SeqTelemetryStore.cs`
- `src/backend/Eling.Backend/Telemetry/SeqTelemetryRuntime.cs`
- `src/backend/Eling.Backend/Telemetry/SeqForwardingSink.cs`
- `src/backend/Eling.Backend/Telemetry/ElingMeters.cs`
- `src/backend/Eling.Backend/Telemetry/ElingActivitySource.cs`
- `src/backend/Eling.Backend/Telemetry/SeqProbe.cs`
- `src/backend/Eling.Backend/Endpoints/TelemetryEndpoints.cs`

Backend, modified:

- `Mcp/Telemetry/ToolTelemetryFilter.cs` — record meters and a span
- `Mcp/McpLoggingExtensions.cs` — register the forwarding sink
- `Bootstrap/DashboardRoutes.cs` — map the telemetry endpoints
- `Bootstrap/McpHostBuilder.cs`, `Bootstrap/DashboardServices.cs` — register
  the store and the runtime hosted service
- `Eling.Backend.csproj` — `Serilog.Sinks.Seq`, `OpenTelemetry`,
  `OpenTelemetry.Exporter.OpenTelemetryProtocol`

Frontend:

- `src/app/dashboard/telemetry/page.tsx`, `Telemetry.tsx`
- `src/components/app-sidebar.tsx` — nav entry

Tests:

- `SeqTelemetryStoreTests` — template, merge, empty key keeps the stored key
- `SeqTelemetryRuntimeTests` — disabled builds nothing, re-apply disposes
- `TelemetryEndpointsTests` — GET/PUT round-trip, key never returned
- `ElingMetersTests` — counters and histograms recorded, via `MeterListener`
- `ToolTelemetryFilterTests` — one span and one counter per call, error path

## Calls I made, for review

1. **Traces are in scope.** "Maximise Datalust" reads as all three signals,
   and traces are the cheapest to add because the filter already exists.
2. **Config applies at process start**, with an immediate rebuild in the
   serving process. Documented in the UI, not hidden.
3. **Runtime gauges come only from the dashboard-owning process**, to avoid
   N-times duplication across MCP hosts.
4. **15-second export interval**, between Seq's documented 10s example and
   the existing 2s SSE cadence, which is too chatty for a remote server.
5. **Server URL, not full endpoint**, is stored; the code appends
   `/ingest/otlp/v1/metrics` and `/ingest/otlp/v1/traces`.
6. **Local log files stay.** Seq is additive.

## Left to the builder

- Confirm the shape of `GET {serverUrl}/api` on a real Seq and parse the
  version from it; fall back to `unknown` when it does not parse.
- Confirm the exact `Serilog.Sinks.Seq` buffer and retry options for the
  installed package version rather than guessing at option names.
- Decide whether `eling.tool.payload` earns its place once the first real
  charts exist; drop it if not.
