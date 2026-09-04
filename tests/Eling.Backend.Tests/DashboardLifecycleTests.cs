using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace Eling.Backend.Tests;

/// <summary>
/// Pecut 9 dashboard coordinator lifecycle, exercised through real processes:
/// N projects = N eling processes + exactly one eling-dashboard on an isolated test dashboard port.
/// Same collection as McpProcessTests (sequential) because they contend for the
/// same port. Liveness intervals are shortened via env vars.
/// </summary>
[Collection("ProcessTests")]
public sealed class DashboardLifecycleTests : IAsyncLifetime
{
    private static readonly TimeSpan LifecycleTimeout = TimeSpan.FromSeconds(20);

    private readonly List<Process> _processes = [];
    private readonly List<string> _tempDirs = [];
    private readonly List<System.Collections.Concurrent.ConcurrentQueue<string>> _stderrLines = [];
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(2), BaseAddress = new Uri(TestProcesses.BaseUrl) };

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var process in _processes)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                process.Dispose();
            }
            catch
            {
                // Best effort cleanup.
            }
        }

        _client.Dispose();

        // Wait for the dashboard to release port 4317 so the next test starts clean.
        var deadline = DateTime.UtcNow + LifecycleTimeout;
        while (DateTime.UtcNow < deadline && await TestProcesses.DashboardAliveAsync(_client))
        {
            await Task.Delay(200);
        }

        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private Process StartRuntime()
    {
        var dir = Path.Combine(Path.GetTempPath(), "eling-lifecycle-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        // Real projects carry their own .eling; without one, ProjectScope walks
        // up past the temp dir and may land on an ancestor's store (e.g. a live
        // session rooted at the user profile).
        Directory.CreateDirectory(Path.Combine(dir, ".eling"));
        _tempDirs.Add(dir);

        var process = TestProcesses.Start(TestProcesses.HostDll, dir, args: [], env: TestProcesses.TestTimingEnv);
        _processes.Add(process);
        var stderrLines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        _stderrLines.Add(stderrLines);
        _ = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                stderrLines.Enqueue(line);
            }
        });
        return process;
    }

    private string DumpDiagnostics()
    {
        var all = string.Join("\n", _stderrLines.SelectMany(q => q.ToArray()));
        return all.Length > 3000 ? all[..3000] : all;
    }

    /// <summary>Closes stdin so the MCP runtime exits cleanly and unregisters.</summary>
    private static async Task GracefulStopAsync(Process process)
    {
        try
        {
            process.StandardInput.Close();
        }
        catch
        {
            // Already gone.
        }

        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    private async Task<int> WaitForDashboardAsync()
    {
        var pid = await TestProcesses.WaitForDashboardPidAsync(_client, LifecycleTimeout);
        Assert.True(pid.HasValue, $"Dashboard did not become healthy. Runtime stderr:\n{DumpDiagnostics()}");
        return pid.Value;
    }

    /// <summary>
    /// Waits until no dashboard answers on 4317, so every test starts from a
    /// known state regardless of what the previous test left behind.
    /// </summary>
    private async Task WaitForCleanPortAsync()
    {
        var consecutiveDown = 0;
        var deadline = DateTime.UtcNow + LifecycleTimeout;
        while (DateTime.UtcNow < deadline && consecutiveDown < 3)
        {
            consecutiveDown = await TestProcesses.DashboardAliveAsync(_client) ? 0 : consecutiveDown + 1;
            await Task.Delay(200);
        }

        Assert.True(consecutiveDown >= 3, $"Port {TestProcesses.TestDashboardPort} never settled before the test started.");
    }

    private async Task<List<JsonElement>> WaitForRuntimeCountAsync(int expected)
    {
        List<JsonElement>? last = null;
        var deadline = DateTime.UtcNow + LifecycleTimeout;
        while (DateTime.UtcNow < deadline)
        {
            last = await TestProcesses.TryGetRuntimesAsync(_client);
            if (last is { Count: var count } && count >= expected) return last;
            await Task.Delay(200);
        }

        return last ?? [];
    }

    private async Task<List<JsonElement>> WaitUntilRuntimeGoneAsync(int processId)
    {
        List<JsonElement>? last = null;
        var deadline = DateTime.UtcNow + LifecycleTimeout;
        while (DateTime.UtcNow < deadline)
        {
            last = await TestProcesses.TryGetRuntimesAsync(_client);
            if (last is not null && last.All(r => r.GetProperty("processId").GetInt32() != processId)) return last;
            await Task.Delay(200);
        }

        return last ?? [];
    }

    [Fact]
    public async Task StartRuntime_FirstInstance_StartsDashboardAndRegistersSelf()
    {
        await WaitForCleanPortAsync();
        var runtime = StartRuntime();
        var dashboardPid = await WaitForDashboardAsync();

        var runtimes = await WaitForRuntimeCountAsync(1);
        var entry = Assert.Single(runtimes);

        Assert.Equal(runtime.Id, entry.GetProperty("processId").GetInt32());
        Assert.Equal(Path.GetFullPath(_tempDirs[0]), entry.GetProperty("projectRoot").GetString());
        Assert.True(Directory.Exists(entry.GetProperty("dataDirectory").GetString()));
        // In the merged single-binary architecture, the backend process IS the
        // dashboard owner: /health returns Environment.ProcessId, which is also
        // the runtime's pid. They are intentionally equal.
        Assert.Equal(dashboardPid, runtime.Id);
    }

    [Fact]
    public async Task StartRuntime_SecondInstance_ReusesExistingDashboard()
    {
        await WaitForCleanPortAsync();
        var first = StartRuntime();
        var dashboardPid = await WaitForDashboardAsync();
        await WaitForRuntimeCountAsync(1);

        var second = StartRuntime();
        await WaitForRuntimeCountAsync(2);

        // The peer also self-registers via mcpHost in Program.cs, so both
        // owner and peer are reported in runtimes. We assert the dashboard pid is
        // unchanged (no second owner took over) and that the second process
        // can still be gracefully stopped.
        var currentPid = await TestProcesses.WaitForDashboardPidAsync(_client, TimeSpan.FromSeconds(5));
        Assert.Equal(dashboardPid, currentPid);

        var runtimes = await TestProcesses.GetRuntimesAsync(_client);
        var roots = runtimes.Select(r => r.GetProperty("projectRoot").GetString()).ToHashSet();
        Assert.Contains(Path.GetFullPath(_tempDirs[0]), roots);  // owner registered
        Assert.Contains(Path.GetFullPath(_tempDirs[1]), roots);  // peer registered

        // Both processes are still alive: second is MCP-only, first is owner.
        Assert.False(first.HasExited, "Owner must still be alive.");
        Assert.False(second.HasExited, "Peer must still be alive (MCP-only).");

        await GracefulStopAsync(second);
        await GracefulStopAsync(first);
    }

    [Fact]
    public async Task StartRuntime_MultipleSimultaneous_ConvergesToSingleDashboard()
    {
        await WaitForCleanPortAsync();
        // Three runtimes race; every loser must exit cleanly instead of killing
        // the winner, and all three must end up registered on the single winner.
        var starters = Enumerable.Range(0, 3).Select(_ => Task.Run(() => StartRuntime())).ToArray();
        await Task.WhenAll(starters);

        var dashboardPid = await WaitForDashboardAsync();
        await WaitForRuntimeCountAsync(3);

        // The winner keeps serving; no second dashboard takes over.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var currentPid = await TestProcesses.WaitForDashboardPidAsync(_client, TimeSpan.FromSeconds(2));
            Assert.Equal(dashboardPid, currentPid);
        }

        foreach (var process in _processes.ToList())
        {
            await GracefulStopAsync(process);
        }
    }

    [Fact]
    public async Task StopRuntime_FirstInstance_RemovesRuntimeKeepsDashboardAlive()
    {
        await WaitForCleanPortAsync();
        var first = StartRuntime();
        await WaitForDashboardAsync();
        await WaitForRuntimeCountAsync(1);

        var second = StartRuntime();
        await WaitForRuntimeCountAsync(2);

        await GracefulStopAsync(first);

        var remaining = await WaitUntilRuntimeGoneAsync(first.Id);
        var survivor = Assert.Single(remaining);
        Assert.Equal(second.Id, survivor.GetProperty("processId").GetInt32());

        // Dashboard still serving for the surviving runtime.
        Assert.True(await TestProcesses.DashboardAliveAsync(_client));

        await GracefulStopAsync(second);
    }

    [Fact]
    public async Task StopRuntime_LastInstance_StopsDashboard()
    {
        await WaitForCleanPortAsync();
        var runtime = StartRuntime();
        await WaitForDashboardAsync();
        await WaitForRuntimeCountAsync(1);

        await GracefulStopAsync(runtime);

        var deadline = DateTime.UtcNow + LifecycleTimeout;
        while (DateTime.UtcNow < deadline && await TestProcesses.DashboardAliveAsync(_client))
        {
            await Task.Delay(200);
        }

        Assert.False(await TestProcesses.DashboardAliveAsync(_client),
            "Dashboard must shut down after the last runtime disappears.");
    }

    [Fact]
    public async Task KillRuntime_WithoutUnregister_EventuallyDisappearsFromRegistry()
    {
        await WaitForCleanPortAsync();
        var runtime = StartRuntime();
        await WaitForDashboardAsync();
        await WaitForRuntimeCountAsync(1);

        // Crash-style death: no graceful unregister, liveness sweeper must reap it.
        runtime.Kill(entireProcessTree: true);

        var remaining = await WaitUntilRuntimeGoneAsync(runtime.Id);
        Assert.Empty(remaining);
    }

    /// <summary>
    /// Verifies the in-place port takeover: when the owner backend exits, a
    /// MCP-only peer that lost the initial port race must promote itself to
    /// owner via the poll loop, not stay MCP-only forever. The promoted peer
    /// then serves /health on the same port within the takeover interval.
    /// </summary>
    [Fact]
    public async Task StartRuntime_OwnerExits_PeerPromotesToOwner()
    {
        await WaitForCleanPortAsync();

        // Backend #1 wins the race → owner mode: binds the port, serves HTTP,
        // and /health reports its own ProcessId.
        var first = StartRuntime();
        var ownerPid = await WaitForDashboardAsync();
        await WaitForRuntimeCountAsync(1);

        // Backend #2 sees the port taken → MCP-only peer (no HTTP surface).
        var second = StartRuntime();
        await WaitForRuntimeCountAsync(2);

        // The owner still answers /health after the peer joins.
        var currentPid = await TestProcesses.WaitForDashboardPidAsync(_client, TimeSpan.FromSeconds(5));
        Assert.Equal(ownerPid, currentPid);

        // Graceful shutdown of the owner. The peer's poll loop may promote
        // itself as soon as the OS releases the port, so the port might
        // already be bound by the peer before this wait returns.
        await GracefulStopAsync(first);

        // Bounded observation: the peer should now serve HTTP on the same
        // port (promoted itself to owner) within ELING_TAKEOVER_MS + margin.
        // We do NOT assert an intermediate "port free" state because the
        // takeover may be effectively instantaneous; the end state is what
        // matters.
        var tookOverPid = await TestProcesses.WaitForDashboardPidAsync(_client, TimeSpan.FromSeconds(10));
        Assert.NotNull(tookOverPid);
        Assert.NotEqual(ownerPid, tookOverPid!.Value);
        Assert.Equal(second.Id, tookOverPid.Value);

        // Promotee's runtime registry should now show 1 alive runtime (the peer itself).
        // The original owner should NOT reappear.
        var runtimes = await WaitForRuntimeCountAsync(1);
        var entry = Assert.Single(runtimes);
        Assert.Equal(second.Id, entry.GetProperty("processId").GetInt32());

        await GracefulStopAsync(second);
    }
}
