# Desktop Chat Liveness & Progress (design)

**Date:** 2026-11-19
**Size:** small-to-medium (one cohesive change, client + one backend fix)
**Status:** proposed — awaiting approval

## Intent / why

When a chat turn runs in the desktop, the UI goes quiet between Send and the first
visible token — often 30–100 s for reasoning models behind an OpenAI-compatible
gateway. The gateway streams SSE with ~1.5 s keepalive chunks while the model thinks, but reasoning content is
not surfaced as text deltas, so the whole reply pops in at the end. Users read this as
"hang / krik2". Additionally, user messages render full-width left-aligned, which reads
less naturally than a chat layout.

**Verified 2026-11-19:** gateway headers ~2.4 s, keepalive keep-alive chunks, content
deltas arrive as discrete SSE events (18 deltas in ~135 ms for a short reply). No
streaming failure markers in backend logs for the observed turns.

## Goals

1. The transcript always shows *something moving* while a turn is in flight.
2. User messages read as chat bubbles on the right; Eling/Tool stay left.
3. A turn whose provider stalls must not permanently degrade the backend process.

## Non-goals / anti-goals

- No guarantee of token-by-token text (provider/model controlled).
- No new backend endpoint, no new SSE event type in this change (the client works with
  events that already exist: started / delta / tool / tool_result / done / failed).
- No change to history rendering, code blocks, tool expand/collapse.
- No commit/branch strategy decided here — docs/spec untouched by code changes.

## Changes

### A. Desktop UI (`ChatView.axaml`, `ChatViewModel.cs`, maybe `ChatMessageRow.cs`)

- **Bottom busy bar:** while a turn is in flight, a slim bar pinned above the input
  shows an indeterminate progress strip + the live status text ("Menunggu model… Ns" /
  "Menjalankan tool: recall…" / "Menulis…" / "Selesai — N tool call(s)"). The transcript
  itself stays pure content — no thinking row inserted into the message list — so the
  indicator survives scrolling and never shifts around tool rows. The bar is always
  visible during busy and collapses back to a plain status line when idle.
- **Tool rows auto-collapse:** when a new tool result lands during a turn, only the
  newest tool row is expanded; every earlier tool row in the same turn (and session)
  is collapsed. Keeps long tool-call chains readable instead of a wall of expanded
  JSON. History loading is already collapsed and stays as-is.

- **Live status line (replaces blank `StatusText` during busy):**
  - Send → `Menunggu model… {elapsed}s` (1 s timer via a DispatcherTimer, started in
    `BeginTurn`, stopped in `EndTurn`);
  - `StreamTool` → `Menjalankan tool: {name}…`;
  - `StreamToolResult` → back to `Menunggu model… {elapsed}s` (elapsed keeps counting);
  - `StreamDelta` → `Menulis…` (only if non-trivial gap; otherwise timer keeps running);
  - `StreamDone` → `Selesai — {n} tool call(s)` (or "" when 0);
  - `StreamFailed` / exception → existing message text, unchanged.
- **User bubble (right aligned):** rows with `IsUser` render in a Border aligned right,
  capped at ~75 % of the transcript width, keeping the existing `#2563eb`-family fill.
  Eling and Tool rows unchanged (left, full-width).
- No layout system changes: same grid rows, same ScrollViewer, same chrome.

### B. Backend fix (`Agent/Infrastructure/Ai/MeaiChatGateway.cs`)

- `StreamFirstByteTimeout` (20 s) must not kill streaming for the whole process.
  Replace the permanent per-endpoint `StreamingBrokenByEndpoint` kill-switch with
  **per-turn fallback**: on first-byte timeout, throw as today; the existing
  `CompleteStreamingWithFallbackAsync` catch already falls back to `CompleteAsync`,
  which emits the full text as one delta. The only broken part is the process-wide
  "mark endpoint dead" behaviour and the 20 s budget.
- Raise first-byte budget to 60 s (reasoning models routinely think >20 s).
- Remove the `StreamingBrokenByEndpoint` set-on-timeout; keep a per-turn marker in the
  log only. Keep `StreamingBrokenByEndpoint` type only if needed for explicit
  provider-reported non-streaming (not implemented now — YAGNI until seen).
- Add one backend unit test: gateway stalls before first byte → turn still completes
  via fallback and produces final text; a second turn still attempts streaming.

### C. Logging

- One informational line when a turn falls back (`Agent turns: provider streaming
  fell back to single-shot`), so the next "why is it all at once" is answerable from
  logs. Deltas stay unlogged (too noisy).

## Approaches considered

- **A: dots-only placeholder.** Rejected: silent for tool-heavy turns and no elapsed
  feedback; undersells B for the same code size.
- **B (picked): thinking row + live status + elapsed timer, client-side only for the
  UI parts.** Matches "no krik2" exactly, zero risk to the stream path.
- **C: new backend heartbeat SSE event.** More truthful "provider is alive" signal,
  but touches envelope/DTOs/desktop and is unnecessary once keepalives/timer exist.
  Revisit only if keepalive-based liveness is wanted later.
- **Provider-side change (e.g. reasoning budget/headers):** out of scope; the gateway
  is the user's own infra, not this repo.

## Constraints

- Tests: desktop xunit project only for desktop; backend tests for the gateway change;
  never whole-solution runs. Desktop rebuild + relaunch is part of the loop (user
  preference: auto kill + rebuild + relaunch, no asking).
- Lite/changed tests only, per global standing rule.
- No new silent catches; every `catch` logs.
- Desktop UI must keep fixed chrome (header/list/footer), only transcript scrolls.
