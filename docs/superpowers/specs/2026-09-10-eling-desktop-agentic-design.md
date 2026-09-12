# Eling Desktop Agentic Transformation — Thin Spec

Date: 2026-09-10
Status: draft v3 — backend-owned, desktop is pure frontend, lib behind own ports (awaiting user review)
Scope: `src/desktop/Eling.Desktop` (UI only) + new backend agent endpoints
under `src/backend/Eling.Backend` (`Endpoints/AgentEndpoints.cs` + small services).
No library types outside the adapter folder (see LLM seam).

## WHY

Eling Desktop currently only lists memories. The goal is a thin agentic desktop:
open one or more folders, chat against them with real tools, configure the
user's own OpenAI-compatible provider, and keep the existing memory list as one
menu instead of the whole app.

Key constraint (user decision): the desktop is a pure frontend. Every operation
— file access, LLM calls, provider config, chat history — lives in the backend.
The backend may run on another PC while the desktop runs on this PC, so the
desktop must never touch the workspace filesystem directly, never hold the LLM
key, and never call the provider directly. It only talks HTTP to the backend
base URL (the existing supervisor-probed URL, plus a manual override for the
remote-PC case).

Thin means: no streaming, no scheduler, no plugin system, no shell execution.
Smallest backend surface that makes Chat + Folders + Settings work remotely.

## Decisions

- "Pecut" = real tools executed server-side and callable from chat:
  `read_file`, `list_dir` (backend-machine filesystem, containment-checked),
  `recall_memory`, `save_memory` (reuse existing memory services).
- Folders = multi-root tree where roots live on the backend machine. Desktop
  only renders listings/previews/edits returned over HTTP.
- Provider = user-owned custom OpenAI-compatible endpoint, configured and
  stored in the backend (key never leaves the server except for user entry).
  Model list is fetched backend-side from `GET {provider}/models`.
- Chat history is stored backend-side per workspace; desktop only renders it.
- Memories view: current memory UI moved as-is into a menu. No behavior change.

## UX layout (unchanged surface, new plumbing)

Sidebar left, content right. Four menus: Chat (default), Folders, Memories,
Settings. Bottom status bar keeps the existing connection/SSE display and adds
backend URL + provider status (endpoint host + selected model or
"not configured").

Chat view: workspace picker (active root), message list (user / assistant /
visible tool-call rows), input + Send, busy state while the backend turn runs.
No streaming in v1 — one request, one final reply (backend may do up to 3
internal tool hops before answering).

Folders view: workspace roots with Add/Remove (paths are backend-machine
paths), lazy tree (one level at a time), text preview (size-capped),
Edit/Save for text files. Binary/oversize refused with a note, never raw bytes.

Settings view: backend URL override (empty = auto via supervisor probe),
provider base URL, API key (password box, stored server-side), Fetch Models,
model dropdown (manual entry fallback), Test ping, Save. Desktop never
persists the LLM key locally.

## Architecture (backend-owned)

```text
Desktop (pure UI, HTTP only)
  ChatViewModel    -> ElingApiClient.AgentChat*    -> backend /api/agent/chats*
  FoldersViewModel -> ElingApiClient.AgentFiles*   -> backend /api/agent/workspaces*, /api/agent/files*
  SettingsViewModel-> ElingApiClient.AgentProvider*-> backend /api/agent/provider*
  MemoriesViewModel-> existing memory calls (unchanged)

Backend (all operations)
  Endpoints/AgentEndpoints.cs  (/api/agent/* route group, same style as MemoryEndpoints;
                                depends only on the ports below, never on lib types)
  Agent/Ports/                (OUR abstractions — the only thing endpoints/services touch)
    IChatGateway.cs            (single-shot completion: messages + tool defs -> text and/or tool requests)
    IModelCatalog.cs           (list models for a provider config)
    IAgentTool.cs              (one interface per tool: name, description, schema, ExecuteAsync)
  Agent/Services/
    ProviderStore.cs           (provider baseUrl + model + key, server-side storage)
    AgentTurnService.cs        (OWNS the tool loop, hop cap 3; calls IChatGateway, runs IAgentTool, persists)
    WorkspaceRegistry.cs       (registered roots on backend machine + containment checks)
    BackendFileTools.cs        (list_dir / read_file / write_file with caps + atomic write)
    BackendChatStore.cs        (history per workspace, backend-machine files)
    AgentTools/                (ReadFileTool, ListDirTool via file tools; RecallMemoryTool,
                                SaveMemoryTool via IMemoryService — all behind IAgentTool)
  Agent/Infrastructure/Ai/     (ONLY place library types may appear)
    MeaiChatGateway.cs         (IChatGateway backed by Microsoft.Extensions.AI + OpenAI bridge)
    HttpModelCatalog.cs        (IModelCatalog via tiny HttpClient GET {provider}/models)

No new backend binary, no new port. Same owner-port model and route wiring
(`DashboardRoutes` gains one `MapAgentEndpoints()` call next to the memory
routes). SSE `memories`/`runtimes` events keep working; agent scopes may add a
`chats` event later (deferred).

## LLM seam (anti-lock-in — user decision)

Endpoints and application services never reference the AI library directly.
They program against OUR ports (`IChatGateway`, `IModelCatalog`, `IAgentTool`)
with OUR DTOs. The library (`Microsoft.Extensions.AI` + OpenAI bridge) lives
exclusively inside `Agent/Infrastructure/Ai/` as one replaceable adapter.

Concretely: the single-shot loop is owned by `AgentTurnService`, not by the
lib's auto-invocation middleware — the gateway returns "text plus tool
requests", our service executes tools and re-calls, max 3 hops. So swapping
libraries means writing one new adapter class implementing the same two
interfaces plus one DI registration line; endpoints, turn loop, tools,
stores, and all tests stay untouched. Enforcement is by code review in v1
(no lib usings outside the adapter folder; analyzer deferred).

`GET /models` stays a tiny `HttpClient` inside `HttpModelCatalog` (model
listing is not part of the chat abstraction), which also keeps that seam
independent of any chat lib.

## Backend API v1 (exact surface)

Provider (server-side secrets):

- `GET /api/agent/provider` -> `{ baseUrl, model, hasKey, modelsCached[] }`
  (never returns the key).
- `PUT /api/agent/provider` `{ baseUrl, model?, apiKey? }` — empty apiKey
  keeps the stored one; empty model keeps current.
- `POST /api/agent/provider/models` -> `{ models[] }` fetched server-side
  from `GET {provider}/models` (OpenAI shape `data[].id`); shape mismatch
  returns 502 with raw excerpt for the UI to display.
- `POST /api/agent/provider/test` -> tiny `POST {provider}/chat/completions`
  ping; returns `{ ok, message }`.

Workspaces + files (paths are backend-machine paths):

- `GET /api/agent/workspaces` -> `{ roots[] }`.
- `POST /api/agent/workspaces` `{ path }` / `DELETE /api/agent/workspaces`
  `{ path }` — attach/detach only, never delete disk content.
- `GET /api/agent/files/list?root=...&path=...` — one level, capped
  (e.g. 200 entries), dirs first.
- `GET /api/agent/files/read?root=...&path=...` — text only, capped
  (e.g. 64 KB) with `{ content, truncated }`.
- `POST /api/agent/files/write` `{ root, path, content }` — text only, cap
  (e.g. 128 KB), atomic replace, parents created only inside the root.

Chat (turns executed server-side):

- `GET /api/agent/chats?workspace=...` -> chat list headers.
- `GET /api/agent/chats/{id}` -> full message list.
- `POST /api/agent/chats` `{ workspace, message }` -> runs one agentic turn via
  `AgentTurnService`: system prompt (roots + tool descriptions) + capped history
  + input, single-shot `IChatGateway` calls, tools executed through `IAgentTool`
  (max 3 hops), persists, returns `{ chatId, assistant, toolCalls[] }`.
- `POST /api/agent/chats/{id}/messages` — alias for sending into an existing
  chat (same turn logic).

Tool set v1 (server-side only): `list_dir`, `read_file`, `recall_memory`,
`save_memory`. No delete/promote tools, no shell. Unknown tool requests return
a "not available" tool result so the model answers honestly.

## Remote-PC story (why this shape works)

Today the desktop finds the backend via the supervisor probe (dev/staging
ports) or spawns one locally. For the split-PC case the desktop gains one
field — backend URL override in Settings. When set, the desktop skips the
probe/spawn and talks straight to that URL (e.g. `http://<backend-pc>:4417`).
Everything else is identical because every operation is already an HTTP call:
folders resolve on the backend machine, the LLM key lives on the backend
machine, history lives on the backend machine.

Explicit limits for v1: the backend currently binds loopback-oriented ports,
so remote requires the backend to be reachable on the LAN (explicit launch
config + firewall), and v1 ships without a desktop-to-backend auth token.
That means: same-machine = default and safe; remote LAN = explicit opt-in,
trusted network only. A token (or mTLS) is the first hardening item right
after the thin slice, before any use over untrusted networks.

## Guards (backend-enforced, so remote stays safe)

- Every file path must resolve inside a registered root (parent traversal and
  symlink escape rejected, case-normalized).
- Caps enforced server-side regardless of caller: list entries, read bytes,
  write bytes.
- Write is atomic (temp + replace) and text-only; binary refused by sniffing,
  not by extension.
- LLM key is write-only from the desktop's perspective (`hasKey` only).
- Every catch logs server-side and returns a user-visible error; no silent
  failures. No auto-fix loops: failed turns stop and report.

## Memories as a menu

Relocate the existing memory UI verbatim into the Memories menu. Filters,
CRUD, promote/copy modals, SSE refresh unchanged. Zero regression budget here.

## Error handling (user-visible)

- Backend unreachable: desktop status bar red + inline chat/folders notice
  with the attempted base URL.
- Provider 401/bad model/timeout: backend maps to `{ ok: false, message }`
  with the provider's status excerpt; desktop shows it inline and marks the
  provider dot red.
- File errors (missing, outside root, binary, oversize): structured error
  payloads the model can quote; direct file ops show inline text.

## Testing

- Per-csproj runs only (repo rule), never solution-wide chains.
- Backend: endpoint contract tests (provider CRUD without leaking key,
  workspaces attach/detach, list/read/write guards incl. traversal rejection,
  turn loop against a fake `IChatGateway` port incl. hop cap, history
  roundtrip, each `IAgentTool` in isolation). The MEAI adapter gets its own
  narrow tests so replacing it never breaks the suite above.
- Desktop: view-model tests with a fake `HttpMessageHandler` behind
  `ElingApiClient` (no real FS, no real LLM).
- Manual checklist: point desktop at local backend, add 2 roots, preview/edit
  a file, configure provider, fetch models, chat turn that reads a file and
  recalls a memory, restart both and confirm persistence, switch desktop to a
  remote backend URL and repeat read-only steps, Memories menu unchanged.

## Non-goals (v1)

Streaming (SSE/websocket deltas for chat), shell/process tools, scheduler,
plugins, multi-window, embeddings/RAG, shared chat sync, backend auth token
(deferred to the immediate hardening follow-up, not the thin slice).

## Rollout (thin batches)

1. Backend ports + `AgentEndpoints` skeleton + provider store + `HttpModelCatalog`
   + `MeaiChatGateway` adapter (settings works end-to-end, no chat yet) +
   desktop Settings view.
2. Workspace registry + file endpoints + desktop Folders view.
3. Tool router + chat endpoints + chat store + desktop Chat view (4 tools).
4. Shell + Memories relocation + backend-URL override + remote smoke test.

## Self-review

- No TBDs: caps above are initial values adjustable without contract change.
- Consistent: desktop holds no secrets and touches no raw filesystem; every
  operation crosses the existing HTTP boundary, so same-machine and remote-PC
  behave identically.
- Scoped: one route group, four tools, non-streaming, single plan.
- Ambiguity closed: roots/paths/keys/history are backend-machine state;
  removing a root detaches without deleting disk or history; empty backend URL
  means auto-probe, set URL means remote.
