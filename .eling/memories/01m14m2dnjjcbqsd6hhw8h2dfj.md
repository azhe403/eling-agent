---
id: 01m14m2dnjjcbqsd6hhw8h2dfj
type: decision
status: active
tags:
- architecture
- realtime
- sse
- dev-ports
- build-isolation
created_at: 2026-08-28T16:43:25.7469349+00:00
updated_at: 2026-08-28T16:43:25.7469349+00:00
source:
---
For Eling project: Complete Dev Mode & Realtime SSE Architecture:
1. Port scheme: Port 4317 is reserved for Global Staging. Dev mode uses port 4417 (ASP.NET backend coordinator & REST API) and port 4427 (Next.js Live Dev frontend via 'pnpm dev:frontend').
2. Auto-spawn frontend: Eling.Dashboard detects dev mode (port != 4317) and automatically spawns Next.js dev server on port 4427 with non-blocking stream draining.
3. Realtime SSE: Memory mutations from MCP and Dashboard broadcast instant events via Channel<string> at GET /api/events/memories, auto-updating dashboard in realtime.
4. Build/Test isolation: Dev runtime outputs to shared '.bin/' while test runs output to isolated '.bin-test/' (via dotnet test Eling.slnx --artifacts-path .bin-test) avoiding process file locks.
5. Auto-discover local scope: RuntimeRegistry falls back to current workspace .eling store when no runtime has registered yet.