# Eling

Git-native persistent memory engine for AI coding agents.

## The Eling Philosophy

> *"Memories are not stored — they are remembered."*

### The name: *eling*

**Eling** is a Javanese word. It is a verb — small, plain, deeply human — that means **to remember, to be reminded, to come back to one's senses after a moment of forgetfulness**. *Eling* is the soft return of a thought that was, for a moment, lost; it is the act of *being here again, knowing again, in mind again*.

Its opposite in Javanese is *lali* — to forget, to be lost to the moment.

We chose this name deliberately. A memory engine is not, in the end, a database. It is **a practice of returning** — the quiet, repeated ceremony by which a mind comes back to what it once knew. Every time an agent invokes `memory_save`, it is not *writing data*. It is **eling-ing** — committing a small piece of a thought to a tree that will outlive the session, the context window, and perhaps the laptop itself.

And every time the next agent — weeks, months, or years from now — opens the same tree and reads that memory with `cat`, it is **coming back to senses** alongside the original author. A small resurrection, in plain text.

### Why a Git-native memory engine?

A memory that lives only in a database is a memory that **dies with the database**. When the schema changes, the rows are forgotten. When the host crashes, the bits scatter. When the cluster is decommissioned, the wisdom vanishes into the long *lali* of time.

But a memory committed to Git lives **as long as the tree**. It travels with the code. It forks with the team. It surfaces in `git log --grep`. It is reviewable, blameable, diffable, and human-readable to the very last byte. It is — dare we say — *survivable*.

> Git is, in essence, the only database that a thousand engineers can collaboratively read, write, and understand without a tutorial.

### The Five Tenets of Eling

1. **Markdown is the only canonical format.** Every memory is a single `.md` file in `.eling/memories/`. No proprietary schemas, no opaque blobs, no JSON-in-a-database-with-no-history.
2. **The disk is the source of truth.** SQLite is a *cache* — a searchable index, an accelerator. If it disappears, every memory can still be read line-by-line with `cat`.
3. **Dedup is a property of identity, not of storage.** A memory is identified by its ULID, a 128-bit globally-unique handle. Two memories with the same content are *not* the same memory. Two memories with the same ULID *are* the same memory, even if read from different scopes.
4. **Scope is a lens, not a fence.** A memory may live in Project scope (`.eling/memories/`) or Global scope (`~/.config/eling/`). It may be promoted, copied, or moved. What matters is that the *whole* memory is reachable from *somewhere*.
5. **The agent forgets, the tree remembers.** AIs have session lifetimes measured in hours. Repositories have lifetimes measured in decades. Eling is the bridge — a small, disciplined ritual that hands today's thought to tomorrow's mind, byte by byte, in plain text.

> *In a world of vector databases and embedding models, we believe the most powerful memory is the one a human can still read with `cat`.*

### Why "Eling" and not "MemoryDB" or "RecallEngine"?

Because a name should evoke the **act**, not the **artifact**. The database, the API, the index — these are merely instruments of the practice. The practice itself is what we celebrate.

When you invoke `memory_save`, you are not *writing* — you are *eling-ing*. You are committing a small piece of a thought to a tree that will outlive your context window, your session, and possibly your laptop.

And when the next agent — weeks, months, or years from now — opens this same tree and reads that memory with `cat`, they will not see a JSON blob or a vector index. They will see **plain English**, in **plain Markdown**, with a **plain ULID** at the top.

That is the Eling promise. A memory you can still read with your eyes.

*Eling* — to remember, to come back to one's senses, to be here again. May your agents always eling, and never lali.

---

## Architecture

- `src/backend/Eling.Core`: Domain (Memory, Intention, Scope, Runtime) — `Eling.Core/` → `Memory/`, `Scope/`, `Runtime/`, `MemoryRecall/`; flat `Eling.Core` namespace. No infrastructure beyond `Ulid`, `YamlDotNet`, `Microsoft.Data.Sqlite`.
- `src/backend/Eling.Backend`: Single binary `eling-backend` — unified MCP (stdio) + HTTP API + UI (`Microsoft.NET.Sdk.Web`, `Assembly: eling-backend`). `Program.cs` only orchestrates; `Bootstrap/` (port probe `DashboardPort`, scope `ProjectContext`, DI `DashboardServices`, routes `DashboardRoutes`, self-register `RuntimeSelfRegistration`); `Mcp/Tools/` (one logical tool per file e.g. `MemoryWriteTool`); `Dtos/` (single `Eling.Backend.Dtos`); `Endpoints/`, `Converters/`. Listens `127.0.0.1:4317` (stg) / `4417` (dev, `ELING_DASHBOARD_PORT`) + `::1`; `0.0.0.0:4427` for frontend dev via `pnpm`.
- `src/frontend/Eling.Dashboard`: Next.js frontend UI. Dev `next dev -H 0.0.0.0 -p 4427` → `rewrites` `nginx`-style proxy to `http://127.0.0.1:{ELING_BACKEND_PORT}/api/*`. Prod `output: export` served as `eling-dashboard-ui/` static by `Eling.Backend`.

HTTP ports (single-binary ownership: first launcher wins, peers skip Kestrel):
- `4317` staging (global `~/.local/bin/eling-backend`), `4417` dev (`Eling.Backend.csproj`), `4427` frontend dev.

See [INSTALL.md](INSTALL.md) for binary + MCP setup and [CHANGELOG.md](CHANGELOG.md) for history.

## Build & Test

### Backend
```bash
# Dev build (outputs to shared .bin/)
dotnet build Eling.slnx
dotnet build src/backend/Eling.Backend/Eling.Backend.csproj -p:ElingSkipDashboard=true  # fast, skip pnpm

# Run tests (one project at a time, isolated .bin-test/ per AGENTS.md)
dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test
dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --artifacts-path .bin-test
```

### Frontend
```bash
pnpm --prefix src/frontend/Eling.Dashboard install
pnpm --prefix src/frontend/Eling.Dashboard build   # or pnpm dev → 4427 → backend 4417
```

`validate-eling.ps1` runs the full flow (build → tests → dashboard HTTP API → stdio MCP); scripts now target `eling-backend.exe`.
