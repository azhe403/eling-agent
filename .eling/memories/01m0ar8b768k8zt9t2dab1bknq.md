---
id: 01m0ar8b768k8zt9t2dab1bknq
type: decision
status: active
tags:
- architecture
- context
- splitting
- eling
created_at: 2026-08-18T15:36:18.9191931+00:00
updated_at: 2026-08-18T15:36:18.9191931+00:00
source:
---
For Eling project, split per logical context:
- `Eling.Host` handles HTTP/web server concerns (REST API, HTTP MCP, Kestrel)
- `Eling.Mcp` manages MCP-specific logic (logging, protocol handling)
- `Eling.Server` contains pure domain/model logic (DTOs, endpoints, tests)

Rationale: Clean separation of concerns prevents circular dependencies, enables independent testing, and makes services more maintainable. Each layer should have single responsibility.