# Diagnostic Log Viewer — Design Spec

**Date:** 2026-10-23
**Status:** Implemented (2026-10-23)
**Scope:** Backend REST endpoints + frontend dashboard page + MCP tool reuse

---

## 1. Problem

Eling writes runtime diagnostics to rolling daily log files (`backend.log`, `backend-YYYY-MM-DD.log`, etc.) under the central log directory. Today there is no in-product way to inspect them:

- Agents must read the raw file via `file_read`/`file_read_any` (no filtering, no tail semantics, no rotation awareness).
- Humans must open the file on disk (large, unstructured, no level coloring).

Goal: provide a **dashboard log viewer** (history + live tail) and a **reusable MCP tool** so both humans and agents can diagnose errors in the Eling infrastructure.

## 2. Goals / Non-Goals

**Goals**
- View backend logs from the dashboard with filtering (stream, date, level, substring) and tail.
- Live-tail new lines in real time via SSE (file-based polling, cross-process).
- Reuse the same read logic in an MCP tool (`log_read`, `log_list`) for agents.
- Level-colored, auto-scroll-aware log panel.

**Non-Goals**
- No log ingestion pipeline / structured JSON logging (Serilog stays text-template).
- No cross-host aggregation; only the local machine's central log dir.
- No log mutation (clear/truncate) in v1 (read-only). Download/export optional (see §9).
- No desktop stream in v1 (backend only; desktop later).

## 3. Architecture

### 3.1 Data source
- `CentralLogDirectory.Resolve()` → `~/.local/share/eling/logs` (honors `ELING_DATA_DIR`).
- Files: `{stream}.log` (active) and `{stream}-YYYY-MM-DD.log` (archives). Stream ∈ {`backend`, `desktop`} (v1 uses `backend`).
- Written by `RollingDailyFileSink` (active file, 7-day retention, daily rotation).

### 3.2 Backend (Eling.Backend, owner mode only)
New `LogsEndpoints` registered in `DashboardServices` (same as other dashboard endpoints):

| Method | Route | Purpose |
|---|---|---|
| GET | `/api/logs` | List log files for a stream. |
| GET | `/api/logs/read` | Read history (filtered, tailed). |
| GET | `/api/logs/tail` | SSE stream of new lines. |

All read-only. No auth (consistent with `/api/tools`).

### 3.3 Shared read helper
Extract the file-reading/filtering logic from the existing `LogTools` (MCP) into a small shared helper (e.g. `LogReader` in `Eling.Core` or `Eling.Backend`) so MCP and REST do not duplicate. MCP tool keeps its JSON shape; REST reuses the same core.

### 3.4 Frontend
- New page `src/app/dashboard/logs/page.tsx`.
- Sidebar entry "Logs" in `app-sidebar.tsx`.
- New hook `use-log-tail.ts` for SSE.

## 4. API Contracts

### 4.1 `GET /api/logs`
Query: `stream?` (default `backend`).
Response:
```json
{ "directory": "...", "count": 2, "files": [
  { "name": "backend.log", "sizeBytes": 89591078, "lastWriteTimeUtc": "..." },
  { "name": "backend-2026-10-22.log", "sizeBytes": 64123906, "lastWriteTimeUtc": "..." }
]}
```

### 4.2 `GET /api/logs/read`
Query: `stream?`, `date?` (YYYY-MM-DD; empty → active), `level?`, `contains?`, `tail?` (default 500, clamp 1–5000).
Response:
```json
{
  "file": "backend.log",
  "directory": "...",
  "totalLines": 519840,
  "filteredTotal": 12,
  "returned": 6,
  "lines": [ "2026-... [ERR] ...", "..." ]
}
```
- `totalLines`: total lines in file.
- `filteredTotal`: lines passing level/contains filters.
- `returned`: `min(tail, filteredTotal)`.
- `lines`: the tailed subset (plain Serilog-formatted strings).

### 4.3 `GET /api/logs/tail` (SSE)
Query: `stream?`, `level?`, `contains?`, `tail?` (initial backlog, default 50).
Content-Type: `text/event-stream`.
Behavior:
1. On connect, send the last `tail` matching lines as `data: <json-line>\n\n` (one event per line, JSON-encoded string).
2. Then poll the file every 500 ms; when size grew, read new bytes, split into lines, filter, emit each as an event.
3. If file size shrank (rotation/recreate), reset read position to 0.
4. On client disconnect (`CancellationToken`), stop.
5. Heartbeat comment `: ping\n\n` every 15 s to keep proxies alive.

Event payload: `{"line":"..."}` (single field) so the client can append.

## 5. MCP Tool (reuse)
Existing `LogTools` (`log_list`, `log_read`) in `Eling.Backend.Mcp.Tools` remain. They reuse the shared helper. No streaming variant (MCP is request/response); `log_read` with `tail` covers agent needs. Optional future: `log_search` (regex across archives) — out of v1 unless requested.

## 6. Frontend UX

### 6.1 Page layout
- Toolbar (top, wraps on narrow screens):
  - Stream selector (backend) — v1 single option, future desktop.
  - Date selector: "Today (active)" or specific date (drives `date` param).
  - Level dropdown: All / DEBUG / INFO / WARN / ERROR / FATAL.
  - Search input (contains, debounced 300 ms).
  - Tail count: 100 / 500 / 1000 / 5000.
  - "Refresh" button (re-fetch history).
  - "Live" toggle button (green when on).
- Body: scrollable monospace panel (`font-mono`, small), level-colored lines:
  - ERR/FATAL → red (`--destructive`)
  - WARN → amber (`--warning`)
  - INF → default
  - DBG → muted (`--muted-foreground`)
- Footer: `Showing {returned} of {filteredTotal} lines · {file}` + last fetch time + live status dot.

### 6.2 Auto-scroll / tail behavior
- Auto-scroll to bottom is **on** only when the panel is at the bottom (within ~4 px).
- When user scrolls up: auto-scroll off. Live stream keeps receiving but does not scroll; show floating "↓ N new lines" pill + "Scroll to bottom" button.
- Clicking pill/button scrolls to bottom and re-enables auto-scroll.
- When Live is off, "Refresh" re-fetches history and scrolls to bottom.

### 6.3 Empty / loading
- Loading: small spinner in toolbar.
- Empty: "No matching log lines."

### 6.4 SSE hook (`use-log-tail.ts`)
- Mirrors `use-memories-sse.ts` pattern: `EventSource`, status `connected/connecting/error`, cleanup on unmount.
- Parses each `data:` as JSON `{line}` and appends to an array (capped at 10 000 lines client-side to avoid memory blow-up).
- Applies level/contains filters client-side too (server also filters) for consistency.

## 7. Security
- Read-only endpoints. No auth (local dashboard, consistent with `/api/tools`).
- Paths are resolved server-side from `CentralLogDirectory`; no user-supplied path (prevents traversal). `stream` is validated against an allow-list (`backend`, `desktop`).
- `date` validated as `YYYY-MM-DD` or empty.

## 8. Testing
- Backend: unit tests for `LogReader` (filter, tail, missing file, rotation detection) and endpoint integration tests (list/read/tail SSE smoke).
- Frontend: component tests for level coloring and auto-scroll toggle (optional, manual smoke acceptable for v1).
- Existing `LogToolsTests` (4 tests) remain green.

## 9. Open questions (decide before coding)
1. Download/export log from dashboard? (v1: no; can add "Copy visible" button.)
2. Clear/truncate log? (v1: no — risk; revisit if needed.)
3. Sidebar icon: `FileText` vs `Terminal`? (propose `FileText`.)
4. Should `/api/logs/tail` send initial backlog (default 50) or start empty? (propose 50.)

## 10. Risks
- Polling 500 ms per SSE connection; acceptable for local single-user dashboard.
- Active file is 89 MB today; reading tail must seek from end (use `FileStream` + seek), not load whole file. The shared helper must tail efficiently (read last chunk, not entire file) for large files.
- Rotation: active file replaced at midnight; tail endpoint must handle shrink (reset position) and file-not-found (retry).
- Client memory: cap at 10 000 lines.

## 11. Implementation order (after approval)
1. Extract shared `LogReader` from `LogTools`; update `LogTools` to use it; keep tests green.
2. Add backend endpoints (`/api/logs`, `/api/logs/read`, `/api/logs/tail`).
3. Register endpoints in `DashboardServices`.
4. Frontend: page + hook + sidebar entry.
5. Tests + manual smoke.

---

*Draft — awaiting approval. No code changes beyond this spec until approved.*
