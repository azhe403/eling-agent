# Realtime Coordinator (Open Projects / Runtimes) via SSE Implementation Plan

> **Goal:** Eliminate all polling to `GET /api/coordinator/runtimes` by streaming runtime lifecycle events (`data: runtimes`) over Server-Sent Events (SSE) from the Coordinator to the Next.js Dashboard.

**Tech Stack:** ASP.NET Core Minimal APIs, System.Threading.Channels, C# 13 / .NET 10, Next.js 16 (React 19 / TypeScript), EventSource API.

---

## Global Constraints

- Zero polling: Eliminate `focus` / `visibilitychange` window event listeners that trigger manual runtime re-fetching.
- Backward compatibility: Keep the existing SSE channel `/api/events/memories` as the unified event stream for both memory mutations and runtime updates.
- Resilient sweeping: Broadcaster must notify when `RuntimeRegistry.Sweep()` prunes stale runtimes.
- All tests must pass: `dotnet test Eling.slnx --artifacts-path .bin-test`.

---

### Task 1: Add Runtime Event Broadcasting in Coordinator & Registry

**Files:**
- Modify: `src/backend/Eling.Dashboard/CoordinatorEndpoints.cs`
- Modify: `src/backend/Eling.Dashboard/RuntimeRegistry.cs`
- Test: `tests/Eling.Dashboard.Tests/MemoryApiTests.cs`

**Steps:**
- [ ] **Step 1: Broadcast `"runtimes"` on Register and Unregister**
  In `CoordinatorEndpoints.cs`:
  - On `POST /api/coordinator/register` -> call `broadcaster.Notify("runtimes")`.
  - On `DELETE /api/coordinator/unregister/{pid}` -> call `broadcaster.Notify("runtimes")`.

- [ ] **Step 2: Broadcast `"runtimes"` on Sweeper Pruning**
  In `RuntimeRegistry.cs`:
  - Pass/inject `MemoryChangeBroadcaster?` or callback when runtimes are pruned in `Sweep()`.
  - Trigger `broadcaster.Notify("runtimes")` whenever stale runtimes are removed.

- [ ] **Step 3: Add integration tests**
  In `MemoryApiTests.cs`:
  - Test that registering and unregistering runtimes emits the `"runtimes"` SSE event.

---

### Task 2: Frontend Zero-Polling & Realtime Runtime Updates

**Files:**
- Modify: `src/frontend/Eling.Dashboard/src/app/dashboard/memories/page.tsx`

**Steps:**
- [ ] **Step 1: Wire `"runtimes"` event in `EventSource.onmessage`**
  In `memories/page.tsx`:
  - When `event.data === "runtimes"` (or mutation events), call `loadRuntimes()` and `load()`.

- [ ] **Step 2: Remove window event listeners (focus/visibilitychange)**
  In `memories/page.tsx`:
  - Remove `window.addEventListener("focus", onFocus)` and `visibilitychange` handler.
  - Keep initial `loadRuntimes()` on mount only.

- [ ] **Step 3: Verify TypeScript and Frontend Build**
  - Run `pnpm --prefix src/frontend/Eling.Dashboard build` to verify clean compilation.

---

### Task 3: Full Verification

- [ ] **Step 1: Run complete test suite**
  - Run `dotnet test Eling.slnx --artifacts-path .bin-test`
  - Expected: 100% tests passing.
