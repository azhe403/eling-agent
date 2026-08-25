# Eling

Git-native persistent memory engine for AI coding agents.

## Architecture

- `src/backend/Eling.Core`: Pure domain abstractions & interfaces (zero infrastructure dependencies).
- `src/backend/Eling.Application`: Memory/intention services, Markdown file storage, SQLite/FTS5 index cache.
- `src/backend/Eling.Mcp`: MCP server protocol adapters (stdio).
- `src/backend/Eling.Dashboard`: ASP.NET Core HTTP host exposing coordinator & memory APIs.
- `src/backend/Eling.Host`: `eling` entry point — project-scoped MCP runtime over stdio; ensures & heartbeats the dashboard on port 4317.
- `src/frontend/Eling.Dashboard`: Next.js frontend UI communicating via HTTP API.

## Build & Test

### Backend
```bash
dotnet build Eling.slnx --artifacts-path .artifacts
dotnet test Eling.slnx --artifacts-path .artifacts
```

### Frontend
```bash
pnpm --prefix src/frontend/Eling.Dashboard install
pnpm --prefix src/frontend/Eling.Dashboard build
```
