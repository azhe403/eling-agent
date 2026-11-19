using CliWrap;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

namespace Eling.Backend.Tests;

/// <summary>
/// Shared plumbing for process-based tests: launches the built eling /
/// eling-dashboard binaries directly via dotnet exec (no dotnet run — MSBuild
/// output would pollute the MCP stdout stream).
/// </summary>
public static class TestProcesses
{
    /// <summary>
    /// Root of the Eling repo. Resolved by walking up from the test assembly's
    /// base directory until a directory containing <c>Eling.slnx</c> is found,
    /// so the lookup stays correct regardless of the output layout (.bin\Debug\net10.0\,
    /// .bin\net10.0\, etc.).
    /// </summary>
    public static string RepoRoot { get; } = ResolveRepoRoot();

    private static string ResolveRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 10 && dir is not null; depth++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Eling.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        // Fallback for when the SDK layout differs.
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
    }

    public static string HostDll { get; } = ResolveHostBinary("eling-backend.dll");

    /// <summary>
    /// Resolves a product binary. Products are built into the dev output root,
    /// not into the per-project test folder, which now holds test assemblies
    /// only — so the output root decides, never the file timestamps. Ranking
    /// both roots by mtime let a stale copy in the test root shadow the real
    /// build, which is the opposite of what a test should exercise.
    /// </summary>
    private static string ResolveHostBinary(string binaryName)
    {
        var configured = Environment.GetEnvironmentVariable("ELING_OUTPUT_ROOT");
        var roots = new[]
        {
            string.IsNullOrWhiteSpace(configured) ? ".bin" : configured,
            ".bin",
            ".bin-test",
        };

        foreach (var root in roots)
        {
            var path = Path.Combine(RepoRoot, root, binaryName);
            if (File.Exists(path)) return path;
        }

        return Path.Combine(RepoRoot, roots[0], binaryName);
    }

    /// <summary>Fast liveness intervals so lifecycle tests finish in seconds.</summary>
    public static IDictionary<string, string> TestTimingEnv => new Dictionary<string, string>
    {
        ["ELING_TEST_HEARTBEAT_MS"] = "300",
        ["ELING_TEST_SWEEP_MS"] = "200",
        ["ELING_TEST_STALE_MS"] = "800",
        ["ELING_TEST_GRACE_MS"] = "1000",
        ["ELING_TEST_SHUTDOWN_DEBOUNCE_MS"] = "500",
        ["ELING_TEST_TAKEOVER_MS"] = "300"
    };

    /// <summary>
    /// Isolated dashboard port for the whole process-test run: real eling
    /// instances from other sessions own 4317, so tests must never touch it.
    /// All process tests share one sequential collection, so one port is safe.
    /// </summary>
    public static readonly int TestDashboardPort = FindFreePort(45000, 46000);

    /// <summary>
    /// Isolated user-scope root for the whole process-test run. Real eling
    /// instances from other sessions write runtime registrations into the
    /// global ~/.config/eling/runtime, and a dashboard born on any port still
    /// syncs those files on startup. Pointing every spawned process at a
    /// throwaway temp user scope keeps the test dashboard fully separate from
    /// running instances so the two can never interfere with each other.
    /// </summary>
    public static readonly string TestUserScope = CreateTestUserScope();

    /// <summary>
    /// A live test run keeps its scope directory fresh: the spawned runtimes
    /// heartbeat into <c>runtime/</c> every few hundred milliseconds. So a
    /// scope dir quiet for this long belongs to a run that is gone — a hard
    /// kill, a crashed testhost — and is safe to sweep from a later run.
    /// </summary>
    private static readonly TimeSpan AbandonedScopeAge = TimeSpan.FromHours(2);

    static TestProcesses()
    {
        // Delete leftovers from runs that died without cleaning up, then take
        // this run's own scope with us when the testhost exits — the normal
        // case is a clean exit, and a hard kill is what the age guard covers.
        SweepAbandonedUserScopes();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DeleteTree(TestUserScope);
    }

    private static void SweepAbandonedUserScopes()
    {
        try
        {
            var cutoff = DateTime.UtcNow - AbandonedScopeAge;
            foreach (var dir in Directory.EnumerateDirectories(Path.GetTempPath(), "eling-usertest-*"))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(dir) < cutoff)
                    {
                        Directory.Delete(dir, recursive: true);
                    }
                }
                catch (Exception ex)
                {
                    TestLogs.Log("test cleanup: sweep stale temp dir failed", ex);
                }
            }
        }
        catch (Exception ex)
        {
            TestLogs.Log("test cleanup: sweep temp dirs failed", ex);
        }
    }

    private static void DeleteTree(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex)
        {
            TestLogs.Log("test cleanup: delete temp tree failed", ex);
        }
    }

    private static string CreateTestUserScope()
    {
        var root = Path.Combine(Path.GetTempPath(), "eling-usertest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "runtime"));
        return root;
    }

    public static string BaseUrl => $"http://127.0.0.1:{TestDashboardPort}";

    private static int FindFreePort(int min, int max)
    {
        for (var candidate = min; candidate <= max; candidate++)
        {
            try
            {
                var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, candidate);
                listener.Start();
                listener.Stop();
                return candidate;
            }
            catch (Exception ex)
            {
                TestLogs.Log("find free port: port occupied, trying next", ex);
            }
        }

        throw new InvalidOperationException($"No free port in [{min}, {max}].");
    }

    /// <summary>
    /// Spawns the built backend as an isolated MCP runtime via CliWrap, the
    /// same long-lived-child pattern <c>FrontendDevSpawner</c> uses. The child's
    /// stdin is <see cref="TestStdinStream"/>, and its stdout/stderr arrive on
    /// <see cref="TestProcess.Stdout"/> / <see cref="TestProcess.Stderr"/> as
    /// line queues completed when the process exits.
    /// </summary>
    public static TestProcess Start(string dllPath, string workingDirectory, IEnumerable<string>? args = null, IDictionary<string, string>? env = null)
    {
        var arguments = new List<string> { "exec", dllPath };
        if (args is not null) arguments.AddRange(args);

        var stdout = new BlockingCollection<string>(new ConcurrentQueue<string>());
        var stderr = new BlockingCollection<string>(new ConcurrentQueue<string>());
        var stdin = new TestStdinStream();
        Process? process = null;

        var spawn = Cli.Wrap("dotnet")
            .WithArguments(arguments)
            .WithWorkingDirectory(workingDirectory)
            .WithEnvironmentVariables(builder =>
            {
                foreach (var (key, value) in env ?? new Dictionary<string, string>())
                    builder.Set(key, value);
                // Every spawned runtime + its child dashboard use the isolated port.
                builder.Set("ELING_DASHBOARD_PORT", TestDashboardPort.ToString());
                // And an isolated user scope so the test dashboard never syncs runtime
                // registrations from a live Eling instance owned by another session.
                builder.Set("ELING_USER_SCOPE", TestUserScope);
                // Same reasoning for the workspace catalogue, the shared data
                // directory and the test dashboard port: see amendment A2 —
                // without these, spawned processes write rows/audit/codebase
                // chunks into the developer's real machine-global stores.
                builder.Set("ELING_AGENT_DATA_DIR", TestUserScope);
                builder.Set("ELING_DATA_DIR", TestUserScope);
            })
            .WithStandardInputPipe(CliWrap.PipeSource.FromStream(stdin, autoFlush: true))
            .WithStandardOutputPipe(CliWrap.PipeTarget.ToDelegate(line => stdout.Add(line)))
            .WithStandardErrorPipe(CliWrap.PipeTarget.ToDelegate(line => stderr.Add(line)))
            .ExecuteAsync(
                startInfo => startInfo.CreateNoWindow = true,
                configureProcess: child => process = child)
            .Task;

        // End of child: both output queues complete, and the exit gets logged
        // if it failed (kill shows up here as an exception; that is expected
        // during teardown and only reported once).
        _ = spawn.ContinueWith(
            task =>
            {
                stdout.CompleteAdding();
                stderr.CompleteAdding();
                if (task.Exception is not null)
                    TestLogs.Log("test process exited", task.Exception.GetBaseException());
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return new TestProcess(
            process ?? throw new InvalidOperationException("Failed to start process."),
            stdout,
            stderr,
            stdin);
    }

    /// <summary>
    /// Reads one line from the child's output queue, or null once the child has
    /// exited and the queue is drained and completed.
    /// </summary>
    public static async Task<string?> ReadLineAsync(BlockingCollection<string> queue, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (queue.TryTake(out var line)) return line;
            if (queue.IsCompleted) return null;
            if (cancellationToken.IsCancellationRequested) return null;
            await Task.Delay(5, CancellationToken.None);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static async Task<int?> WaitForDashboardPidAsync(HttpClient client, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var loggedWait = false;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var response = await client.GetAsync("/health");
                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
                    return body.GetProperty("pid").GetInt32();
                }
            }
            catch (Exception ex)
            {
                if (!loggedWait)
                {
                    loggedWait = true;
                    TestLogs.Log("wait for health: server not up yet", ex);
                }
            }

            await Task.Delay(200);
        }

        return null;
    }

    public static async Task<List<JsonElement>> GetRuntimesAsync(HttpClient client)
    {
        var runtimes = await client.GetFromJsonAsync<JsonElement[]>("/api/coordinator/runtimes", JsonOptions);
        return runtimes?.ToList() ?? [];
    }

    /// <summary>Like GetRuntimesAsync but returns null when the dashboard is
    /// momentarily unreachable (restarting / shutting down).</summary>
    public static async Task<List<JsonElement>?> TryGetRuntimesAsync(HttpClient client)
    {
        try
        {
            return await GetRuntimesAsync(client);
        }
        catch
        {
            return null;
        }
    }

    public static async Task<bool> DashboardAliveAsync(HttpClient client)
    {
        try
        {
            using var response = await client.GetAsync("/health");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
