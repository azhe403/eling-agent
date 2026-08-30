# Realtime Memory Refresh via Server-Sent Events (SSE) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement explicit event-driven real-time memory refresh on the Eling dashboard using Server-Sent Events (SSE), notifying the frontend immediately whenever a memory mutation occurs via either Dashboard REST API or MCP tools.

**Architecture:** An in-memory SSE broadcaster (`MemoryChangeBroadcaster`) in `Eling.Dashboard` streams events to `/api/events/memories`. Dashboard endpoints trigger it directly on mutation, while `Eling.Host` (MCP server) notifies the coordinator via `POST /api/coordinator/notify-change` without file system watchers. The frontend (`memories/page.tsx`) listens via `EventSource` and auto-reloads.

**Tech Stack:** ASP.NET Core Minimal APIs, System.Threading.Channels, C# 13 / .NET 10, Next.js 16 (React 19 / TypeScript), EventSource API.

## Global Constraints

- Never use FileSystemWatcher — all change notifications must be explicit via method calls and coordinator HTTP POST.
- MCP operations must not block or fail if the Dashboard is offline; notification must be resilient and fire-and-forget.
- Clean separation of concerns: `IMemoryChangeNotifier` abstraction in `Eling.Application`, concrete broadcaster in `Eling.Dashboard`, HTTP notifier in `Eling.Host`.
- All tests must pass: `dotnet test Eling.slnx --artifacts-path .bin-test` and frontend build `pnpm --prefix src/frontend/Eling.Dashboard build`.
- Project hygiene: no absolute paths or personal names in code/configs.

---

### Task 1: Add IMemoryChangeNotifier Abstraction to Eling.Application

**Files:**
- Create: `src/backend/Eling.Application/IMemoryChangeNotifier.cs`
- Modify: `src/backend/Eling.Application/Eling.Application.csproj`

**Interfaces:**
- Produces: `IMemoryChangeNotifier` interface with `Task NotifyAsync(string reason = "mutation", CancellationToken cancellationToken = default);` and `NullMemoryChangeNotifier` singleton.

- [x] **Step 1: Write `IMemoryChangeNotifier.cs`**

```csharp
namespace Eling.Application;

public interface IMemoryChangeNotifier
{
    Task NotifyAsync(string reason = "mutation", CancellationToken cancellationToken = default);
}

public sealed class NullMemoryChangeNotifier : IMemoryChangeNotifier
{
    public static readonly NullMemoryChangeNotifier Instance = new();

    public Task NotifyAsync(string reason = "mutation", CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
```

- [x] **Step 2: Build application project to verify compilation**

Run: `dotnet build src/backend/Eling.Application/Eling.Application.csproj`
Expected: Build succeeded.

---

### Task 2: Implement MemoryChangeBroadcaster and SSE Endpoints in Eling.Dashboard

**Files:**
- Create: `src/backend/Eling.Dashboard/MemoryChangeBroadcaster.cs`
- Modify: `src/backend/Eling.Dashboard/CoordinatorEndpoints.cs`
- Modify: `src/backend/Eling.Dashboard/Program.cs`
- Modify: `src/backend/Eling.Dashboard/Endpoints/MemoryEndpoints.cs`
- Modify: `src/backend/Eling.Dashboard/Endpoints/ScopedMemoryEndpoints.cs`
- Test: `tests/Eling.Dashboard.Tests/MemoryApiTests.cs`

**Interfaces:**
- Consumes: `IMemoryChangeNotifier`
- Produces: `MemoryChangeBroadcaster` singleton, `GET /api/events/memories` SSE stream, `POST /api/coordinator/notify-change` endpoint.

- [x] **Step 1: Create `src/backend/Eling.Dashboard/MemoryChangeBroadcaster.cs`**

```csharp
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Eling.Application;

namespace Eling.Dashboard;

public sealed class MemoryChangeBroadcaster : IMemoryChangeNotifier
{
    private readonly object _lock = new();
    private readonly List<Channel<string>> _subscribers = [];

    public void Notify(string reason = "mutation")
    {
        lock (_lock)
        {
            foreach (var sub in _subscribers)
            {
                sub.Writer.TryWrite(reason);
            }
        }
    }

    public Task NotifyAsync(string reason = "mutation", CancellationToken cancellationToken = default)
    {
        Notify(reason);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<string> SubscribeAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        lock (_lock)
        {
            _subscribers.Add(channel);
        }

        try
        {
            while (await channel.Reader.WaitToReadAsync(cancellationToken))
            {
                while (channel.Reader.TryRead(out var item))
                {
                    yield return item;
                }
            }
        }
        finally
        {
            lock (_lock)
            {
                _subscribers.Remove(channel);
            }
            channel.Writer.TryComplete();
        }
    }
}
```

- [x] **Step 2: Register Broadcaster and SSE route in `Program.cs` & `CoordinatorEndpoints.cs`**

In `src/backend/Eling.Dashboard/Program.cs`:
Register `MemoryChangeBroadcaster` as singleton and map `GET /api/events/memories`.

In `src/backend/Eling.Dashboard/CoordinatorEndpoints.cs`:
Add `POST /api/coordinator/notify-change`.

- [x] **Step 3: Wire Broadcaster calls in `MemoryEndpoints.cs` & `ScopedMemoryEndpoints.cs`**

Inject `MemoryChangeBroadcaster` into mutation endpoints (Create, Update, Delete, Promote, Copy) and call `broadcaster.Notify("dashboard")` upon success.

- [x] **Step 4: Add integration test for `/api/events/memories` and `/api/coordinator/notify-change` in `MemoryApiTests.cs`**

Verify that `POST /api/coordinator/notify-change` returns 200 OK and mutations trigger notification.

- [x] **Step 5: Run tests**

Run: `dotnet test tests/Eling.Dashboard.Tests/Eling.Dashboard.Tests.csproj`
Expected: PASS

---

### Task 3: Wire MCP MemoryTools Notification in Eling.Mcp & Eling.Host

**Files:**
- Create: `src/backend/Eling.Host/HttpCoordinatorMemoryChangeNotifier.cs`
- Modify: `src/backend/Eling.Mcp/MemoryTools.cs`
- Modify: `src/backend/Eling.Mcp/McpServiceExtensions.cs`
- Modify: `src/backend/Eling.Host/Program.cs`
- Test: `tests/Eling.Mcp.Tests/MemoryToolsTests.cs`

**Interfaces:**
- Consumes: `IMemoryChangeNotifier`
- Produces: `HttpCoordinatorMemoryChangeNotifier` sending fire-and-forget HTTP POST to `http://127.0.0.1:{DashboardPort}/api/coordinator/notify-change`.

- [x] **Step 1: Create `src/backend/Eling.Host/HttpCoordinatorMemoryChangeNotifier.cs`**

```csharp
using Eling.Application;

namespace Eling.Host;

public sealed class HttpCoordinatorMemoryChangeNotifier : IMemoryChangeNotifier
{
    private readonly int _port;
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(2) };

    public HttpCoordinatorMemoryChangeNotifier(int port)
    {
        _port = port;
    }

    public async Task NotifyAsync(string reason = "mutation", CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await HttpClient.PostAsync(
                $"http://127.0.0.1:{_port}/api/coordinator/notify-change",
                content: null,
                cancellationToken);
        }
        catch
        {
            // Dashboard is optional; fire-and-forget
        }
    }
}
```

- [x] **Step 2: Update `MemoryTools.cs` to inject `IMemoryChangeNotifier` and call `NotifyAsync`**

In `src/backend/Eling.Mcp/MemoryTools.cs`:
Inject `IMemoryChangeNotifier? notifier = null` (fallback `NullMemoryChangeNotifier.Instance`).
After `SaveAsync`, `UpdateAsync`, `DeleteAsync`, `CopyToProjectAsync`, `PromoteToGlobalAsync`, call `await _notifier.NotifyAsync("mcp");`.

- [x] **Step 3: Register `HttpCoordinatorMemoryChangeNotifier` in `Eling.Host/Program.cs`**

Register `builder.Services.AddSingleton<IMemoryChangeNotifier>(new HttpCoordinatorMemoryChangeNotifier(DashboardPort));`.

- [x] **Step 4: Run MCP tests to verify compatibility**

Run: `dotnet test tests/Eling.Mcp.Tests/Eling.Mcp.Tests.csproj`
Expected: PASS

---

### Task 4: Connect SSE Realtime Refresh in Dashboard Frontend

**Files:**
- Modify: `src/frontend/Eling.Dashboard/src/app/dashboard/memories/page.tsx`

**Interfaces:**
- Consumes: SSE endpoint `/api/events/memories`
- Produces: Automatic `load()` invocation on incoming change event.

- [x] **Step 1: Add `EventSource` listener in `MemoriesPage`**

In `src/app/dashboard/memories/page.tsx`:
Add `useEffect` subscribing to `/api/events/memories`.
On message, call `load()` to refresh memories immediately.
Ensure proper cleanup on component unmount (`es.close()`).

- [x] **Step 2: Build frontend to verify Next.js TypeScript and bundling**

Run: `pnpm --prefix src/frontend/Eling.Dashboard build`
Expected: Build succeeded.

---

### Task 5: End-to-End Verification

**Files:**
- Test all backend and process tests.

- [x] **Step 1: Run complete backend test suite**

Run: `dotnet test Eling.slnx --artifacts-path .bin-test`
Expected: All tests pass (Core, Application, Mcp, Dashboard, Host).

- [x] **Step 2: Run frontend build**

Run: `pnpm --prefix src/frontend/Eling.Dashboard build`
Expected: Static build generated without error.
