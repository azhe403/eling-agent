# Architecture

Single binary, three faces: the same process serves MCP (stdio), the HTTP API, and the dashboard UI. Detailed design notes live in the linked documents; this page is the map.

## Layers

- `src/backend/Eling.Core`: Domain (Memory, Intention, Scope, Runtime) — `Eling.Core/` → `Memory/`, `Scope/`, `Runtime/`, `Recall/`; flat `Eling.Core` namespace. No infrastructure beyond `Ulid`, `YamlDotNet`, `Microsoft.Data.Sqlite`.
- `src/backend/Eling.Backend`: Single binary `eling-backend` — unified MCP (stdio) + HTTP API + UI (`Microsoft.NET.Sdk.Web`, `Assembly: eling-backend`). `Program.cs` only orchestrates; `Bootstrap/` (port probe `DashboardPort`, scope `ProjectContext`, DI `DashboardServices`, routes `DashboardRoutes`, self-register `RuntimeSelfRegistration`); `Mcp/Tools/` (one logical tool per file e.g. `MemoryWriteTool`); `Dtos/` (single `Eling.Backend.Dtos`); `Endpoints/`, `Converters/`. Listens `127.0.0.1:4317` (stg) / `4417` (dev, `ELING_DASHBOARD_PORT`) + `::1`; `0.0.0.0:4427` for frontend dev via `pnpm`.
- `src/frontend/Eling.Dashboard`: Next.js frontend UI. Dev `next dev -H 0.0.0.0 -p 4427` → `rewrites` `nginx`-style proxy to `http://127.0.0.1:{ELING_BACKEND_PORT}/api/*`. Prod `output: export` served as `eling-dashboard-ui/` static by `Eling.Backend`.
- `src/desktop/Eling.Desktop`: Avalonia desktop client (experimental) — standalone memory browsing, runtime monitoring, direct agent chat. Shares the backend DTOs through the HTTP API.

## HTTP ports

Single-binary ownership: first launcher wins, peers skip Kestrel.

- `4317` staging (global `~/.local/bin/eling-backend`), `4417` dev (`Eling.Backend.csproj`), `4427` frontend dev.

## Feature map → detailed docs

- Memory recall engine (hybrid FTS + RRF, overflow summaries): [recall-architecture.md](recall-architecture.md)
- Semantic judge (dedup, merges, fallback posture): [semantic-judge-architecture.md](semantic-judge-architecture.md)
- Agent host setup & MCP registration: [agent-setup.md](agent-setup.md)
- Using Eling day to day: [using-eling.md](using-eling.md)
- Nested/project-local memory scopes: [nested-memory.md](nested-memory.md)
- Desktop ↔ dashboard parity: [desktop-dashboard-parity.md](desktop-dashboard-parity.md)
- Cross-client sorting standard: [sorting-standard.md](sorting-standard.md)
- Release process & asset matrix: [release.md](release.md), local release dry runs: [local-release-simulation.md](local-release-simulation.md)
- Philosophy behind the storage model: [philosophy.md](philosophy.md)

See [INSTALL.md](../INSTALL.md) for binary + MCP setup and [CHANGELOG.md](../CHANGELOG.md) for history.
