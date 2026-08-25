using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace Eling.Host.Tests;

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
    public async Task First_runtime_starts_dashboard_and_registers_itself()
    {
        await WaitForCleanPortAsync();
        var runtime = StartRuntime();
        var dashboardPid = await WaitForDashboardAsync();

        var runtimes = await WaitForRuntimeCountAsync(1);
        var entry = Assert.Single(runtimes);

        Assert.Equal(runtime.Id, entry.GetProperty("processId").GetInt32());
        Assert.Equal(Path.GetFullPath(_tempDirs[0]), entry.GetProperty("projectRoot").GetString());
        Assert.True(Directory.Exists(entry.GetProperty("dataDirectory").GetString()));
        Assert.NotEqual(dashboardPid, runtime.Id); // dashboard is its own process
    }

    [Fact]
    public async Task Second_runtime_reuses_the_same_dashboard()
    {
        await WaitForCleanPortAsync();
        var first = StartRuntime();
        var dashboardPid = await WaitForDashboardAsync();
        await WaitForRuntimeCountAsync(1);

        var second = StartRuntime();
        await WaitForRuntimeCountAsync(2);

        // Same dashboard instance still owns the port.
        var currentPid = await TestProcesses.WaitForDashboardPidAsync(_client, TimeSpan.FromSeconds(5));
        Assert.Equal(dashboardPid, currentPid);

        var roots = (await TestProcesses.GetRuntimesAsync(_client))
            .Select(r => r.GetProperty("projectRoot").GetString())
            .ToHashSet();
        Assert.Contains(Path.GetFullPath(_tempDirs[0]), roots);
        Assert.Contains(Path.GetFullPath(_tempDirs[1]), roots);

        await GracefulStopAsync(second);
        await GracefulStopAsync(first);
    }

    [Fact]
    public async Task Simultaneous_startup_converges_to_exactly_one_dashboard()
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
    public async Task Disconnect_removes_only_that_runtime_and_keeps_dashboard_alive()
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
    public async Task Last_runtime_exit_stops_the_dashboard()
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
    public async Task Dead_runtime_eventually_disappears_from_registry()
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
}
