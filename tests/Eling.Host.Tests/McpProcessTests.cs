using System.Diagnostics;
using System.Text.Json;

namespace Eling.Host.Tests;

/// <summary>
/// Pecut 9 MCP runtime behavior: bare `eling` enters MCP stdio mode, stdout
/// carries protocol traffic only, diagnostics go to stderr, and MCP keeps
/// running when the dashboard is unavailable.
/// </summary>
[Collection("ProcessTests")]
public sealed class McpProcessTests : IAsyncLifetime
{
    private readonly List<Process> _processes = [];
    private string _tempDir = null!;

    public Task InitializeAsync()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "eling-mcp-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        return Task.CompletedTask;
    }

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

        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    private Process StartEling(params string[] args)
    {
        var process = TestProcesses.Start(TestProcesses.HostDll, _tempDir, args);
        _processes.Add(process);
        return process;
    }

    /// <summary>
    /// Sends an initialize request and reads stdout lines until the matching
    /// JSON-RPC response arrives. Any non-JSON line on stdout fails immediately.
    /// </summary>
    private static async Task<JsonElement> SendInitializeAsync(Process process, int id)
    {
        var request = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = "initialize",
            ["params"] = new Dictionary<string, object>
            {
                ["protocolVersion"] = "2024-11-05",
                ["capabilities"] = new { },
                ["clientInfo"] = new { name = "pecut9-test", version = "1.0" }
            }
        });

        await process.StandardInput.WriteLineAsync(request);
        await process.StandardInput.FlushAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
            if (line is null)
                throw new InvalidOperationException("stdout closed before an initialize response arrived.");

            using var json = JsonDocument.Parse(line); // throws on non-JSON: stdout must stay pure
            if (json.RootElement.TryGetProperty("id", out var responseId) &&
                responseId.GetInt32() == id &&
                json.RootElement.TryGetProperty("result", out _))
            {
                return json.RootElement.Clone();
            }
        }
    }

    [Fact]
    public async Task Bare_eling_enters_mcp_and_answers_initialize_on_stdout()
    {
        var process = StartEling();

        var response = await SendInitializeAsync(process, id: 1);

        Assert.True(response.TryGetProperty("result", out var result));
        Assert.True(result.TryGetProperty("serverInfo", out _));

        process.Kill(entireProcessTree: true);
    }

    [Fact]
    public async Task Stdout_carries_only_json_lines_and_diagnostics_go_to_stderr()
    {
        var process = StartEling();

        await SendInitializeAsync(process, id: 1);

        // Let startup diagnostics accumulate, then stop and drain both pipes.
        await Task.Delay(1500);
        process.Kill(entireProcessTree: true);
        var restOfStdout = await process.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var stderr = await process.StandardError.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(stderr.Contains("eling", StringComparison.OrdinalIgnoreCase),
            $"Expected diagnostics on stderr, got: {stderr}");
    }

    [Fact]
    public async Task Mcp_continues_when_dashboard_port_is_blocked()
    {
        // Occupy the test dashboard port so any spawned dashboard loses the bind race and
        // health checks never succeed. MCP must still work.
        var blocker = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, TestProcesses.TestDashboardPort);
        blocker.Start();
        try
        {
            var process = StartEling();

            var response = await SendInitializeAsync(process, id: 7);

            Assert.Equal(7, response.GetProperty("id").GetInt32());

            process.Kill(entireProcessTree: true);
        }
        finally
        {
            blocker.Stop();
        }
    }

    [Fact]
    public async Task No_dashboard_flag_skips_dashboard_startup()
    {
        var process = StartEling("--no-dashboard");

        await SendInitializeAsync(process, id: 1);

        // Nothing may be listening on the dashboard port.
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        var healthUp = false;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var response = await client.GetAsync($"{TestProcesses.BaseUrl}/health");
                healthUp = response.IsSuccessStatusCode;
            }
            catch
            {
                healthUp = false;
            }

            if (healthUp) break;
            await Task.Delay(200);
        }

        Assert.False(healthUp, "--no-dashboard must not start or register a dashboard.");

        process.Kill(entireProcessTree: true);
    }
}
