---
id: 01m0z95pffr2s2kyg6p73e0gy4
type: decision
status: active
tags:
- pecut-10
- scope-aware-memory
- architecture-lock
created_at: 2026-08-26T14:56:46.5818036+00:00
updated_at: 2026-08-26T14:56:46.5818036+00:00
source:
---
For Eling project - Pecut 10 Scope-Aware Memory Management locked approach: Two real scopes Project (.eling) and Global (UserScope ~/.config/eling), MemoryScopeKind enum + MemoryReference (MemoryId+Scope+ProjectRoot), application layer owns scope decisions (IMemoryScopePolicy/Router/Merger), default write -> Project, auto -> Project, MCP scope params (remember project|global|auto, search project|global|merged, default merged with Project priority), Dashboard control plane aggregation via RuntimeRegistry (Global CRUD + Project-routed via alive runtimes + All Open Projects virtual view preserving scope identity), no filesystem scanning, no cross-project MCP access, storage remains separated.