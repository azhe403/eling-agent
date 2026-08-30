---
id: 01m1721xy24rtbbda8s3bmexvs
type: decision
status: active
tags:
- realtime_sse
- cross_env
- bug_observation
- future_plan
- filesystem_watcher
- architecture
created_at: 2026-08-29T15:26:18.5625473+00:00
updated_at: 2026-08-29T15:26:18.5625473+00:00
source:
---
[Insight] Realtime SSE Cross-Environment Behavior:
Save memory via eling_dev (port 4417) tidak trigger SSE event di dashboard Staging (port 4317) dan sebaliknya.
Penyebab: HttpCoordinatorMemoryChangeNotifier mem-post /api/coordinator/notify-change ke port sendiri saja (satu proses = satu port). Dashboard lain tidak menerima event realtime dari proses tetangga.
Plan (Fase 2): implement Option B (FileSystemWatcher) untuk broadcast lintas env via .eling/memories file watching — aligns dengan SyncFromDisk design yang sudah ada.