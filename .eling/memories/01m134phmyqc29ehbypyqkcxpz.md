---
id: 01m134phmyqc29ehbypyqkcxpz
type: fact
status: active
tags:
- bug
- memory_search
- mcp
- validate-script
- importante
created_at: 2026-08-28T02:55:33.5394183+00:00
updated_at: 2026-08-28T02:55:33.5394183+00:00
source:
---
For Eling project: discovered that the memory_search MCP tool fails in stdio mode. In the Eling.Host stdio MCP server, memory_search with scope=project returns server error "An error occurred invoking 'memory_search'." (isError:true); scope=global hangs/times out; scope=merged then crashes the process. memory_save/tools/list/initialize all work fine. This is a real application bug in the scoped MemoryTools.SearchAsync path (likely the SqliteMemoryIndex global search deadlock or an unhandled exception), NOT a doc/script issue. It was surfaced by the rewritten scripts/validate-eling.ps1 (phase "Stdio MCP mode"). Needs separate investigation. Related: scripts/validate-eling.ps1 was rewritten to match the Host+Dashboard architecture (removed obsolete --port/--root-path/--enable-mcp/--http-mcp flags; dashboard HTTP API phase passes).