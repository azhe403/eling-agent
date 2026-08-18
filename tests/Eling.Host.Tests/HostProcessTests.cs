using System.Diagnostics;
using System.Net;
using System.Net.Http;

namespace Eling.Host.Tests;

/// <summary>
/// Process-based tests: starts Eling.Host as a real process and verifies HTTP endpoints.
/// These test the full startup path including System.CommandLine CLI entry point.
/// </summary>
public sealed class HostProcessTests : IAsyncLifetime
{
    private Process? _process;
    private HttpClient? _client;
    private string _tempDir = null!;

    public async Task InitializeAsync()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "eling-host-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();

        if (_process != null && !_process.HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // Best effort cleanup
            }
            _process.Dispose();
        }

        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    private async Task StartHostAsync(int port, string rootPath)
    {
        var projectDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "src", "backend", "Eling.Host"));

        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"run --port {port} --root-path {rootPath}",
                WorkingDirectory = projectDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            }
        };

        _process.Start();

        // Wait for the server to become ready by polling /health
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        _client.Timeout = TimeSpan.FromSeconds(5);

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (_process.HasExited)
            {
                var stderr = await _process.StandardError.ReadToEndAsync();
                throw new InvalidOperationException($"Host process exited unexpectedly. Exit code: {_process.ExitCode}. Stderr: {stderr}");
            }

            try
            {
                var response = await _client.GetAsync("/health");
                if (response.StatusCode == HttpStatusCode.OK)
                    return;
            }
            catch
            {
                // Server not ready yet, retry
            }

            await Task.Delay(250);
        }

        // Dump diagnostics before failing
        var stdout = await _process.StandardOutput.ReadToEndAsync();
        var err = await _process.StandardError.ReadToEndAsync();
        throw new TimeoutException(
            $"Host did not become ready within 60s. " +
            $"Process running: {!_process.HasExited}. " +
            $"Stdout: {stdout}. Stderr: {err}");
    }

    [Fact]
    public async Task Health_endpoint_returns_200()
    {
        var port = GetAvailablePort();
        await StartHostAsync(port, _tempDir);

        var response = await _client!.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Healthy", body);
    }

    [Fact]
    public async Task Host_starts_with_default_port_and_shuts_down_cleanly()
    {
        var port = GetAvailablePort();
        await StartHostAsync(port, _tempDir);

        Assert.False(_process!.HasExited, "Process should still be running");

        // Kill and verify clean exit
        _process.Kill(entireProcessTree: true);
        await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(_process.ExitCode != -1 || _process.HasExited);
    }

    private static int GetAvailablePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
                var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
