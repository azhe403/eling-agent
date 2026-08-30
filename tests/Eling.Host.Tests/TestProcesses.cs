using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

namespace Eling.Host.Tests;

/// <summary>
/// Shared plumbing for process-based tests: launches the built eling /
/// eling-dashboard binaries directly via dotnet exec (no dotnet run — MSBuild
/// output would pollute the MCP stdout stream).
/// </summary>
public static class TestProcesses
{
    public static string RepoRoot { get; } = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    public static string HostDll { get; } =
        ResolveTestBinary("Eling.Host", "eling.dll");

    public static string DashboardDll { get; } =
        ResolveTestBinary("Eling.Dashboard", "eling-dashboard.dll");

    private static string ResolveTestBinary(string projectName, string binaryName)
    {
        var testArtifact = Path.Combine(RepoRoot, ".bin-test", "bin", projectName, "debug", binaryName);
        if (File.Exists(testArtifact)) return testArtifact;

        var binShared = Path.Combine(RepoRoot, ".bin", "Debug", binaryName);
        if (File.Exists(binShared)) return binShared;

        return testArtifact;
    }

    /// <summary>Fast liveness intervals so lifecycle tests finish in seconds.</summary>
    public static IDictionary<string, string> TestTimingEnv => new Dictionary<string, string>
    {
        ["ELING_TEST_HEARTBEAT_MS"] = "300",
        ["ELING_TEST_SWEEP_MS"] = "200",
        ["ELING_TEST_STALE_MS"] = "800",
        ["ELING_TEST_GRACE_MS"] = "1000",
        ["ELING_TEST_SHUTDOWN_DEBOUNCE_MS"] = "500"
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
            catch
            {
                // Occupied; try next.
            }
        }

        throw new InvalidOperationException($"No free port in [{min}, {max}].");
    }

    public static Process Start(string dllPath, string workingDirectory, IEnumerable<string>? args = null, IDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(dllPath);
        foreach (var argument in args ?? [])
        {
            psi.ArgumentList.Add(argument);
        }
        foreach (var (key, value) in env ?? new Dictionary<string, string>())
        {
            psi.Environment[key] = value;
        }

        // Every spawned runtime + its child dashboard use the isolated port.
        psi.Environment["ELING_DASHBOARD_PORT"] = TestDashboardPort.ToString();
        // And an isolated user scope so the test dashboard never syncs runtime
        // registrations from a live Eling instance owned by another session.
        psi.Environment["ELING_USER_SCOPE"] = TestUserScope;

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start process.");
        return process;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static async Task<int?> WaitForDashboardPidAsync(HttpClient client, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
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
            catch
            {
                // Not up yet.
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
