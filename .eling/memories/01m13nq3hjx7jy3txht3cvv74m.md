---
id: 01m13nq3hjx7jy3txht3cvv74m
type: decision
status: active
tags:
- eling
- dashboard
- sse
- realtime
- architecture
created_at: 2026-08-28T07:52:57.6547166+00:00
updated_at: 2026-08-28T07:52:57.6547166+00:00
source:
---
Eling project decision: Realtime dashboard memory refresh via Server-Sent Events (SSE) without FileSystemWatcher. Dashboard hosts SSE stream at GET /api/events/memories (MemoryChangeBroadcaster backed by Channel<string>). Dashboard mutation endpoints notify the broadcaster directly, while MCP stdio host (Eling.Host) notifies via fire-and-forget POST /api/coordinator/notify-change. Next.js dashboard memories page connects via EventSource and auto-reloads data upon receiving change events.