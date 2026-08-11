# Eling

Git-native persistent memory engine for AI coding agents.

## Architecture

- `src/backend/Eling.Core`: Pure domain abstractions & interfaces (zero infrastructure dependencies).
- `src/backend/Eling.Storage`: Canonical Markdown/JSON file persistence.
- `src/backend/Eling.Index`: SQLite/FTS5 query index cache.
- `src/backend/Eling.Graph`: Graph relationships.
- `src/backend/Eling.Mcp`: MCP server protocol adapters.
- `src/backend/Eling.Server`: ASP.NET Core HTTP host exposing APIs.
- `src/frontend/Eling.Dashboard`: Next.js frontend UI communicating via HTTP API.

## Build & Test

### Backend
```bash
dotnet build Eling.slnx
dotnet test Eling.slnx
```

### Frontend
```bash
pnpm --prefix src/frontend/Eling.Dashboard install
pnpm --prefix src/frontend/Eling.Dashboard build
```
