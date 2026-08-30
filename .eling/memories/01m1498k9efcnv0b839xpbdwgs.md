---
id: 01m1498k9efcnv0b839xpbdwgs
type: decision
status: active
tags:
- architecture
- mcp
- build
- ports
created_at: 2026-08-28T13:34:33.7779571+00:00
updated_at: 2026-08-28T13:34:33.7779571+00:00
source:
---
For Eling project: Dev MCP mode (port 4318) in project opencode.json runs from .csproj via dotnet watch and resolves host/dashboard sibling binaries exclusively from the shared .bin/ folder. .artifacts/ is strictly reserved for isolated test runs (dotnet test --artifacts-path .artifacts). Global MCP (port 4317) runs from standalone global eling binary.