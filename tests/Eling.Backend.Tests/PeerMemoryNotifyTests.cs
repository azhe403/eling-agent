using System.Net;
using System.Net.Sockets;
using Eling.Backend.Bootstrap;
using Eling.Backend.Mcp.Tools;
using Eling.Core.Scope;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Eling.Backend.Tests;

/// <summary>
/// Realtime contract for the cross-workspace topology: the process that bound
/// the dashboard port (possibly another workspace's eling) is the only host
/// serving <c>/api/events/memories</c>, while the workspace where the memory
/// was written runs MCP-only with no SSE subscriber anywhere in that process.
/// A peer MCP save must therefore reach the owner over the HTTP coordinator
/// bridge (<c>HttpCoordinatorMemoryChangeNotifier</c> ->
/// <c>/api/coordinator/notify-change</c> -> "coordinator" topic, which the
/// memory page watches), or the dashboard silently stays stale. This is the
/// regression guard for "dashboard goes stale while eling is held by a peer
/// from another workspace".
/// </summary>
[Collection(ElingDataDirCollection.Name)]
public sealed class PeerMemoryNotifyTests : IAsyncLifetime, IDisposable
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(15);

    private readonly string _peerRoot;
    private readonly string _peerUserScope;
    private readonly List<string> _tempDirs = [];
    private AppServices? _peerServices;

    /// <summary>Dashboard port the owner host bound; free for the test's life.</summary>
    private int Port { get; set; }

    private HttpClient OwnerClient { get; set; } = new();

    private WebApplication? OwnerApp { get; set; }

    public PeerMemoryNotifyTests()
    {
        var root = Path.Combine(Path.GetTempPath(), "eling-peer-notify-" + Guid.NewGuid().ToString("N")[..8]);
        _peerRoot = Path.Combine(root, "peer-workspace");
        _peerUserScope = Path.Combine(root, "peer-user-scope");
        Directory.CreateDirectory(_peerRoot);
        // A real project carries its own .eling; without one the scope chain
        // walks up past the temp dir and may adopt an ancestor's scope.
        Directory.CreateDirectory(Path.Combine(_peerRoot, ".eling"));
        _tempDirs.Add(root);
    }

    public async Task InitializeAsync()
    {
        // Pick a free random port so the test never collides with the dev
        // backend on 4417/4317 or with the lifecycle suite's port.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        // The owner: built and started the same way the production owner is,
        // so the SSE stream and the coordinator endpoint are the real ones.
        OwnerApp = TestAppBuilder.CreateSelfContained(dashboardPort: Port);
        await OwnerApp.StartAsync();
        OwnerClient = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{Port}"),
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public async Task DisposeAsync()
    {
        if (OwnerApp is not null)
        {
            try
            {
                await OwnerApp.StopAsync();
            }
            catch
            {
                /* ignore */
            }

            try
            {
                await OwnerApp.DisposeAsync();
            }
            catch
            {
                /* ignore */
            }

            OwnerApp = null;
        }

        OwnerClient.Dispose();
        _peerServices?.Dispose();
        _peerServices = null;
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            TryDeleteDirectory(dir);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The peer host is another workspace's eling process: built by the same
    /// <see cref="McpHostBuilder"/> production path as Program.cs, pointed at
    /// the owner's dashboard port, but never started — starting it would run
    /// the hosted services (HTTP loop, self-registration) this test does not
    /// want. Only the DI container is exercised, which is exactly the seam
    /// <see cref="MemoryWriteTool"/> resolves its notifier from.
    /// </summary>
    private IHost BuildPeerHost()
    {
        var context = new ProjectContext(
            ScopeChain.Discover(_peerRoot),
            UserScope.Resolve(_peerUserScope),
            Path.Combine(_peerRoot, ".eling"),
            IsUserHome: false);

        var host = McpHostBuilder.Build(context, dashboardPort: Port);
        _peerServices = host.Services.GetRequiredService<AppServices>();
        return host;
    }

    [Fact]
    public async Task PeerMemorySave_BroadcastsToDashboardOwnerStream()
    {
        using var cts = new CancellationTokenSource(FrameTimeout);
        using var response = await OwnerClient.GetAsync(
            "/api/events/memories", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.EnsureSuccessStatusCode();

        await using var body = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(body);

        // Prove the stream is live before asserting the silence would be
        // meaningful: the handshake frame must arrive first.
        var handshake = await reader.ReadLineAsync(cts.Token);
        Assert.Equal("data: connected", handshake);
        await reader.ReadLineAsync(cts.Token); // blank separator

        var peerHost = BuildPeerHost();
        try
        {
            using var scope = peerHost.Services.CreateScope();
            var writeTool = ActivatorUtilities.CreateInstance<MemoryWriteTool>(scope.ServiceProvider);

            // A memory saved from the peer workspace, through the real MCP
            // tool path, not through the dashboard's REST endpoints.
            await writeTool.SaveAsync("cross-workspace realtime probe", "note");

            string frame;
            try
            {
                frame = await ReadFrameAsync(reader, "data: coordinator", cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"No 'data: coordinator' frame arrived on the owner's stream within {FrameTimeout.TotalSeconds}s. " +
                    "A peer workspace's MCP memory save never reached the dashboard owner, so its memory page stays stale.");
            }

            Assert.Equal("data: coordinator", frame);
        }
        finally
        {
            peerHost.Dispose();
        }
    }

    /// <summary>
    /// Reads SSE lines until the expected data frame arrives. Keep-alive pings,
    /// blank separators and unrelated membership noise are skipped; anything
    /// else fails loudly so an ordering or topic regression cannot pass
    /// silently.
    /// </summary>
    private static async Task<string> ReadFrameAsync(
        StreamReader reader, string expected, CancellationToken ct)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(ct)
                       ?? throw new InvalidOperationException(
                           "SSE stream ended before the expected event arrived.");

            if (line == expected) return line;
            if (line.Length == 0 || line == ": ping" || line == "data: runtimes") continue;

            throw new InvalidOperationException(
                $"Unexpected SSE event '{line}' while waiting for '{expected}'.");
        }
    }

    private static void TryDeleteDirectory(string path, int retries = 5, int delayMs = 200)
    {
        if (!Directory.Exists(path)) return;

        for (var i = 0; i < retries; i++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(delayMs);
            }
        }
    }
}
