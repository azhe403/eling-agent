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

- `src/backend/Eling.Core`: Pure domain abstractions & interfaces (zero infrastructure dependencies).
- `src/backend/Eling.Application`: Memory/intention services, Markdown file storage, SQLite/FTS5 index cache.
- `src/backend/Eling.Mcp`: MCP server protocol adapters (stdio).
- `src/backend/Eling.Dashboard`: ASP.NET Core HTTP host exposing coordinator & memory APIs.
- `src/backend/Eling.Host`: `eling` entry point — project-scoped MCP runtime over stdio; ensures & heartbeats the dashboard on port 4317.
- `src/frontend/Eling.Dashboard`: Next.js frontend UI communicating via HTTP API.

## Build & Test

### Backend
```bash
# Dev build (outputs to shared .bin/)
dotnet build Eling.slnx

# Run tests (outputs to isolated .bin-test/)
dotnet test Eling.slnx --artifacts-path .bin-test
```

### Frontend
```bash
pnpm --prefix src/frontend/Eling.Dashboard install
pnpm --prefix src/frontend/Eling.Dashboard build
```
