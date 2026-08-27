# Pecut 10 — Scope-Aware Memory Management + Dashboard Control Plane

============================================================
CONTEXT
============================================================

Pecut 9 establishes:

    eling
        → project-scoped MCP runtime
        → one process per active project

    eling-dashboard
        → shared user-scoped dashboard coordinator
        → one process shared by all active projects

Pecut 9 also establishes:

    ProjectScope
        → nearest .eling

    UserScope
        → OS-appropriate per-user Eling directory

    Runtime Registry
        → active project runtimes known by Dashboard


Pecut 10 introduces multi-scope memory management.

The system now has two REAL memory scopes:

    Project
    Global


IMPORTANT:

    Dashboard may aggregate memories from multiple scopes.

    Dashboard must NOT collapse those scopes into one storage.

    Dashboard aggregation is a VIEW.

    ProjectScope and GlobalScope remain independent ownership boundaries.


============================================================
1. PRIMARY GOAL
============================================================

Implement scope-aware memory management across:

    - Eling.Application
    - MCP memory tools
    - Project memory runtime
    - Global memory
    - Dashboard control plane
    - Dashboard UI/API


Target architecture:

                         Dashboard
                      Control Plane
                            │
            ┌───────────────┼────────────────┐
            │               │                │
            ▼               ▼                ▼
        Global Memory    Project A        Project B
          UserScope       Runtime          Runtime
            │               │                │
            ▼               ▼                ▼
        Global Store      .eling          .eling


============================================================
2. MEMORY SCOPE MODEL
============================================================

Introduce explicit scope identity.

At minimum:

    Project
    Global


Suggested abstraction:

    MemoryScopeKind
    {
        Project,
        Global
    }


A memory returned outside its native store must carry enough
identity to determine where it belongs.

Do NOT assume MemoryId is globally unique across all stores.

Introduce a location/reference concept if needed:

    MemoryReference
    {
        MemoryId
        Scope
        ProjectIdentity? / ProjectRoot?
    }


For Global memory:

    Scope = Global
    Project = null


For Project memory:

    Scope = Project
    Project = concrete project identity


Destructive operations MUST be scope-aware.

This is invalid as a global control-plane contract:

    DELETE /memory/{id}

Prefer a scope-qualified identity, conceptually:

    Global:
        memory location = Global + MemoryId

    Project:
        memory location = ProjectIdentity + MemoryId


============================================================
3. STORAGE TOPOLOGY
============================================================

Memory storage remains physically separated.

Project A:

    /projects/a/.eling/
        → project memories
        → project index


Project B:

    /projects/b/.eling/
        → project memories
        → project index


UserScope:

    <user-data>/eling/
        → global memories
        → global index


Do NOT merge all scopes into one database.

Do NOT make global memory a special row inside every project database.

Do NOT make project memory a special namespace inside the global
database.


============================================================
4. APPLICATION LAYER OWNS SCOPE DECISIONS
============================================================

Scope logic belongs in:

    Eling.Application


NOT in:

    Eling.Mcp
    Eling.Dashboard UI
    SQLite implementation
    index implementation


MCP and Dashboard are adapters.

Storage persists scoped data.

Application coordinates scope policy.


Introduce application-level abstractions as needed, for example:

    IMemoryService
    IMemoryScopePolicy
    IMemoryScopeRouter
    IMemoryMerger


Exact naming is flexible.

Architecture is not.


============================================================
5. WRITE BEHAVIOR
============================================================

A memory write must target a REAL scope.

Supported targets:

    Project
    Global


Recommended write contract:

    scope = project
    scope = global
    scope = auto


Default behavior:

    scope omitted
        → Project


For:

    scope = auto

Pecut 10 may initially resolve:

    auto → Project


Do NOT implement an LLM-based automatic scope classifier in this pecut.


Explicit scope must always win:

    explicit Project
        → Project

    explicit Global
        → Global


The application layer resolves the destination.

The MCP adapter must not directly choose storage based on filesystem paths.


============================================================
6. GLOBAL MEMORY
============================================================

Implement real global memory.

Global memory lives under:

    UserScope


It must support the same essential operations as project memory:

    create
    get
    search
    update
    delete


Global memory must not require any active project runtime.

This is important.


Example:

    eling-dashboard running

        ↓

    manage global memory

must be possible even if:

    active project runtimes = 0


However, project memory remains owned by its project runtime.


============================================================
7. PROJECT MEMORY
============================================================

Project memory remains owned by the project runtime.

Dashboard must NOT directly open and manipulate arbitrary:

    <project>/.eling/

behind the runtime's back.


Project memory flow:

    Dashboard
        ↓
    Dashboard Control Plane
        ↓
    Project Runtime
        ↓
    Application Memory Service
        ↓
    Project .eling storage


This avoids:

    - competing storage ownership
    - dashboard filesystem coupling
    - direct database coupling
    - unclear process authority


If a project runtime is inactive:

    its project memory is NOT considered part of "Open Projects"

Pecut 10 should not invent project-runtime startup solely to browse
closed projects.


============================================================
8. RUNTIME CONTROL-PLANE PROTOCOL
============================================================

Extend the Pecut 9 Dashboard ↔ Runtime connection.

The existing runtime registration/control connection should now support
memory operations needed by Dashboard.

At minimum, Dashboard must be able to request:

    - list/query project memories
    - search project memories
    - get project memory
    - create project memory
    - update project memory
    - delete project memory


Keep the protocol local and minimal.

Do NOT build:

    - gRPC framework
    - distributed message bus
    - remote MCP proxy
    - cloud RPC layer


The protocol is only for:

    Dashboard
        ↔
    local active Eling project runtimes


============================================================
9. DASHBOARD CONTROL PLANE API
============================================================

The dashboard coordinator owns the API consumed by the Dashboard UI.

Suggested conceptual operations:

    Global Memory:

        GET
        POST
        PATCH/PUT
        DELETE
        SEARCH


    Active Project Memory:

        GET
        POST
        PATCH/PUT
        DELETE
        SEARCH


    Aggregated View:

        list/search across:

            Global
            Active Project A
            Active Project B
            ...


Exact route names are implementation detail.


The API response MUST preserve source scope.

Example conceptual response:

    {
        "id": "...",
        "scope": "global",
        "content": "..."
    }


Project example:

    {
        "id": "...",
        "scope": "project",
        "project": {
            "id": "...",
            "root": "..."
        },
        "content": "..."
    }


============================================================
10. DASHBOARD SCOPE SELECTOR
============================================================

Implement a scope-aware Dashboard memory UI.

Conceptually:

    MEMORY SCOPE

    [ Global ]

    [ Project A ]

    [ Project B ]

    [ All Open Projects ]


The active projects list comes from the runtime registry.

Do NOT manually scan the user's filesystem for projects.


The Dashboard should clearly distinguish:

    Global
    Project
    Aggregated


============================================================
11. GLOBAL VIEW
============================================================

When user selects:

    Global


The Dashboard displays only:

    Global memories


Operations:

    Create → Global
    Edit   → Global
    Delete → Global
    Search → Global


Global is a REAL scope.


============================================================
12. PROJECT VIEW
============================================================

When user selects:

    Project A


The Dashboard displays only:

    Project A memory


Operations route through:

    Project A active runtime


The Dashboard must not directly access:

    Project A/.eling


The UI must display clear project identity.


============================================================
13. AGGREGATED VIEW
============================================================

Provide:

    All Open Projects

or equivalent aggregate memory view.


This view aggregates:

    Global memory
    +
    all currently active project runtimes


IMPORTANT:

    This is NOT a MemoryScope.


It is a virtual Dashboard view.


Every result must visibly identify its origin:

    🌐 Global

    📁 Project A

    📁 Project B


Do not silently merge memories into an anonymous list.


============================================================
14. AGGREGATED SEARCH
============================================================

Searching the aggregated Dashboard view should query:

    Global memory
    +
    every active project runtime


Results are then combined for presentation.

Do NOT merge storage.

Do NOT create a central index of every project in this pecut.


A reasonable implementation:

    Dashboard
        → global search

    Dashboard
        → project A search

    Dashboard
        → project B search

        ↓

    aggregate results


The result keeps scope identity.


============================================================
15. WRITING FROM AGGREGATED VIEW
============================================================

The aggregated view has no write scope.

Therefore:

    + Add Memory

from:

    All Open Projects

must require destination selection.


Example:

    Save memory to:

    ( ) Global
    ( ) Project A
    ( ) Project B


Never save to:

    All Open Projects


because it is not a real scope.


============================================================
16. EDITING
============================================================

A memory keeps its original scope.

Examples:

    [Global]
    User prefers concise answers

        Edit
            → Global


    [Project A]
    Uses PostgreSQL

        Edit
            → Project A


The Dashboard must not ask for scope again during ordinary editing.

Scope is part of memory location.


============================================================
17. DELETE
============================================================

Delete must always target the memory's original scope.

Examples:

    delete:
        Global + MemoryId

    delete:
        Project A + MemoryId


Never delete by MemoryId alone from a merged context.


============================================================
18. COPY / PROMOTION BETWEEN SCOPES
============================================================

Do NOT implement implicit scope movement.


Implement explicit operations where practical:

    Copy to Project

    Promote to Global


Recommended semantics:

    Promote to Global:

        Project Memory
            ↓
        copy
            ↓
        Global Memory

        original remains unchanged


    Copy to Project:

        Global Memory
            ↓
        copy
            ↓
        Project Memory

        original remains unchanged


Do NOT automatically delete the source.


Move may be considered later.

Do NOT prioritize Move in Pecut 10.


============================================================
19. AGENT MEMORY SEARCH
============================================================

When MCP is running inside Project A:

    agent memory search default:

        Project A
        +
        Global


This is the agent's retrieval behavior.

The Dashboard aggregated view is separate from agent retrieval.


Flow:

    Agent
        ↓
    Eling.Mcp
        ↓
    Eling.Application
        ↓
    Project Search + Global Search
        ↓
    Merge
        ↓
    Agent Result


============================================================
20. PROJECT PRIORITY
============================================================

For agent search:

    Project scope has higher priority than Global scope.


Example:

Global:

    Preferred language = Python


Project:

    Preferred language = C#


Inside the C# project:

    Project result must rank above Global.


Do NOT delete or modify Global.

Do NOT require exact conflict detection.

Initial implementation may apply scope-aware ranking.


Conceptually:

    Final Rank =
        relevance
        + scope preference


The ranking implementation should remain encapsulated.


============================================================
21. MERGE BEHAVIOR
============================================================

Merge must preserve:

    MemoryId
    Scope
    Project identity where applicable
    original rank/relevance if available


Do not aggressively perform semantic deduplication.


For Pecut 10, safe deduplication is sufficient:

    - same scoped identity
    - optionally exact normalized content


Do NOT add:

    embeddings
    vector database
    semantic similarity

unless already required elsewhere.


============================================================
22. MEMORY TOOL CONTRACT
============================================================

MCP memory tools should support explicit scope where appropriate.


Conceptually:

    remember:
        scope = project | global | auto


    search:
        scope =
            project
            global
            merged


Defaults:

    remember
        → Project

    search
        → Merged


"Merged" means:

    current Project
    +
    Global


It does NOT mean:

    every project on the user's machine.


An MCP runtime may only use:

    its own ProjectScope
    +
    GlobalScope


It must never access:

    Project B

while running for:

    Project A.


============================================================
23. SCOPE SECURITY / ISOLATION
============================================================

Critical invariant:

Project A MCP:

    may access:

        Project A
        Global


Project A MCP must NOT access:

        Project B


Even though both are connected to the same Dashboard.


Dashboard aggregation does not change MCP data authority.


The Dashboard is a control plane.

Each project runtime remains isolated.


============================================================
24. DASHBOARD PROJECT ISOLATION
============================================================

Dashboard can see only active project runtimes through the registry.

For a Project B operation:

    Dashboard
        ↓
    runtime registry lookup
        ↓
    Project B runtime


The Dashboard must not infer arbitrary project locations from:

    filesystem scanning
    git repositories
    .sln
    .slnx


Active runtime registration is the authority.


============================================================
25. FRONTEND UX
============================================================

Do not overbuild the UI.

Minimum useful experience:

    Memory page

    Scope selector:

        Global
        Project A
        Project B
        All Open Projects


    Memory list

    Search

    Create

    Edit

    Delete

    Scope badges


Example:

    ┌────────────────────────────────────┐
    │ Memories                           │
    │                                    │
    │ Scope: [ All Open Projects ▼ ]     │
    │ Search: [____________________]     │
    │                                    │
    │ 🌐 User prefers concise answers    │
    │    Global                          │
    │                                    │
    │ 📁 Uses FTS5                       │
    │    eling-agent-memory              │
    │                                    │
    │ 📁 API uses snake_case             │
    │    Project A                       │
    └────────────────────────────────────┘


Architecture correctness is more important than visual polish.


============================================================
26. KEEP AWAKE — OUT OF SCOPE
============================================================

Do NOT implement:

    Keep Awake
    idle detection
    power management


Pecut 10 only establishes the Dashboard Control Plane pattern that
future features can use.


============================================================
27. GLOBAL MEMORY POLICY — OUT OF SCOPE
============================================================

Do NOT implement:

    automatic memory classification using AI

    automatic promotion from Project to Global

    automatic global deduplication

    automatic conflict resolution


Pecut 10 supports explicit scope.

Policy intelligence can come later.


============================================================
28. CLOSED PROJECTS — OUT OF SCOPE
============================================================

Do NOT implement browsing arbitrary closed projects.


Dashboard aggregation means:

    active/open runtimes only


If a project runtime is gone:

    Dashboard no longer exposes it as an active project.


Do not scan the filesystem to reconstruct it.


============================================================
29. TESTS
============================================================

Add tests for:

SCOPE IDENTITY:

    - Global and Project identity are distinct
    - same MemoryId may be addressed independently by scope
    - destructive operations are scope-qualified


PROJECT ISOLATION:

    - Project A cannot read Project B
    - Project A cannot write Project B
    - Project A cannot delete Project B
    - Project A can read Global


WRITE POLICY:

    - default write targets Project
    - explicit Global targets Global
    - explicit Project targets current Project
    - auto currently resolves to Project


GLOBAL MEMORY:

    - Global memory persists in UserScope
    - Global memory is accessible without project runtime
    - Global CRUD works


PROJECT MEMORY:

    - Project memory remains in .eling
    - Project runtime owns project operations


AGENT SEARCH:

    - default search queries Project + Global
    - Project-only search works
    - Global-only search works
    - Project result ranks above Global where relevance is comparable
    - Project A search never includes Project B


DASHBOARD:

    - Global view returns only Global
    - Project view routes through correct runtime
    - active project registry populates scope selector
    - aggregated view includes Global + active projects
    - every aggregate result preserves scope identity
    - aggregate view does not merge storage
    - add from aggregate requires target scope
    - edit remains in original scope
    - delete remains in original scope
    - project operation targets correct runtime


COPY / PROMOTION:

    - Project → Global copies
    - source remains unchanged
    - Global → Project copies
    - source remains unchanged


RUNTIME AVAILABILITY:

    - inactive project is not shown as open
    - Dashboard does not directly open arbitrary project storage
    - Global memory remains available when no project runtime exists


============================================================
30. BUILD / VALIDATION
============================================================

Before completion:

    dotnet build Eling.slnx

must pass.


Run all tests:

    dotnet test Eling.slnx


All existing tests must remain green.


No regression to:

    Pecut 9 runtime lifecycle

    MCP stdio behavior

    ProjectScope resolution

    UserScope resolution


============================================================
FINAL ACCEPTANCE
============================================================

Assume:

    Global memory:
        "User prefers concise answers"


Project A memory:
        "Use C#"


Project B memory:
        "Use Python"


Project A runtime:

    memory search
        ↓

    sees:

        Project A
        Global

    does NOT see:

        Project B


Project B runtime:

    memory search
        ↓

    sees:

        Project B
        Global

    does NOT see:

        Project A


Dashboard:

    Global
        → Global memory


    Project A
        → Project A memory


    Project B
        → Project B memory


    All Open Projects
        →
        Global
        + Project A
        + Project B

        with every memory showing its source scope


No single shared project-memory database.

No cross-project MCP access.

No filesystem scanning for project discovery.

No scope boundary loss.

No implicit move between scopes.


PRIMARY INVARIANTS:

    PROJECT SCOPE
        = nearest .eling

    GLOBAL SCOPE
        = UserScope

    MCP PROJECT RUNTIME
        = Current Project + Global only

    PROJECT A
        ≠ PROJECT B

    DASHBOARD
        = Control Plane + aggregation

    AGGREGATED VIEW
        = virtual view, NOT memory scope

    DASHBOARD
        must preserve memory origin scope

    DEFAULT WRITE
        = Project

    DEFAULT AGENT SEARCH
        = Project + Global

    PROJECT
        has priority over Global during agent retrieval


OUT OF SCOPE:

    - closed project browsing
    - global AI memory classifier
    - semantic dedup
    - embeddings
    - vector search
    - Keep Awake
    - remote/cloud dashboard
    - arbitrary filesystem project scanning


PECUT 10 COMPLETE WHEN:

    Dashboard can manage:

        Global memory

        + every active project memory

    without breaking:

        storage ownership
        project isolation
        MCP boundaries
        scope identity
