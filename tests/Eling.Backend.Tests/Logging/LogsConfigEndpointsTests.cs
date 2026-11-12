using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Eling.Backend.Bootstrap;
using Eling.Backend.Logging;
using Eling.Core.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Eling.Backend.Tests.Logging;

/// <summary>
/// The log-format endpoints persist through the user-scope
/// <c>LogConfigStore</c> and apply live through the DI switcher, so these
/// tests build the dashboard host the way production does and drive it over
/// HTTP, asserting both the wire payload and the file-level outcome (which
/// active file the sink writes, what got archived on a transition).
/// </summary>
/// <remarks>
/// Joined to <see cref="ElingDataDirCollection"/>: the PUT path mutates the
/// process-global <c>Serilog.Log</c> logger, which the other host-level
/// logging tests in the same collection rely on running serially.
/// </remarks>
[Collection(ElingDataDirCollection.Name)]
public sealed class LogsConfigEndpointsTests : IAsyncLifetime, IDisposable
{
    private WebApplication? _app;
    private HttpClient? _client;
    private Serilog.ILogger? _originalStaticLogger;

    public async Task InitializeAsync()
    {
        _originalStaticLogger = Serilog.Log.Logger;

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        _app = TestAppBuilder.CreateSelfContained(dashboardPort: port);
        await _app.StartAsync();
        _client = new HttpClient(new EnvelopeTestHandler { InnerHandler = new HttpClientHandler() }) { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    }

    [Fact]
    public async Task GetConfig_ReturnsCurrentFormat()
    {
        var res = await _client!.GetAsync("/api/logs/config");
        res.EnsureSuccessStatusCode();

        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("\"format\"", body, StringComparison.OrdinalIgnoreCase);

        // Fresh host, empty user scope: the template defaults to text.
        var doc = JsonDocument.Parse(body);
        Assert.Equal("text", doc.RootElement.GetProperty("format").GetString());
    }

    [Fact]
    public async Task PutConfig_SwitchesFormat_AndArchivesOldActive()
    {
        // Seed a line in the text-era active file so the transition has
        // something to archive.
        SeedActiveLine("text-era-line");

        var put = await _client!.PutAsJsonAsync("/api/logs/config", new { format = "jsonl" });
        put.EnsureSuccessStatusCode();
        var body = await put.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        Assert.Equal("jsonl", doc.RootElement.GetProperty("format").GetString());

        // The live sink now writes jsonl; the text active file was drained
        // into today's archive.
        Assert.Equal(LogFormat.Jsonl, ForwarderInnerFormat());
        var archivePath = ArchivePath(LogFormat.Text);
        Assert.True(File.Exists(archivePath), $"expected archive {archivePath}");
        Assert.Contains("text-era-line", ReadShared(archivePath));
        Assert.False(File.Exists(ActivePath(LogFormat.Text)),
            "the outgoing text active file must be gone after the switch");

        SeedActiveLine("jsonl-era-line");
        Assert.Contains("jsonl-era-line", ReadShared(ActivePath(LogFormat.Jsonl)));

        // And the GET reflects the new value.
        var get = await _client.GetAsync("/api/logs/config");
        get.EnsureSuccessStatusCode();
        var getBody = await get.Content.ReadAsStringAsync();
        Assert.Equal("jsonl", JsonDocument.Parse(getBody).RootElement.GetProperty("format").GetString());
    }

    [Fact]
    public async Task PutConfig_SameFormat_IsNoOp()
    {
        var innerBefore = _app!.Services.GetRequiredService<LogFormatForwardingSink>().Inner;
        var archivePath = ArchivePath(LogFormat.Text);

        var put = await _client!.PutAsJsonAsync("/api/logs/config", new { format = "text" });
        put.EnsureSuccessStatusCode();
        var body = await put.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        Assert.Equal("text", doc.RootElement.GetProperty("format").GetString());

        // No-op: the sink instance is untouched and nothing was archived.
        Assert.Same(innerBefore, _app.Services.GetRequiredService<LogFormatForwardingSink>().Inner);
        Assert.False(File.Exists(archivePath));
    }

    [Fact]
    public async Task PutConfig_UnknownFormat_FallsBackToText()
    {
        var put = await _client!.PutAsJsonAsync("/api/logs/config", new { format = "xml" });

        // The contract: garbage resolves through LogFormat.Resolve, never 400.
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var body = await put.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        Assert.Equal("text", doc.RootElement.GetProperty("format").GetString());

        Assert.Equal(LogFormat.Text, ForwarderInnerFormat());

        // The GET reports the resolved value, not the raw request value.
        var get = await _client.GetAsync("/api/logs/config");
        get.EnsureSuccessStatusCode();
        var getBody = await get.Content.ReadAsStringAsync();
        Assert.Equal("text", JsonDocument.Parse(getBody).RootElement.GetProperty("format").GetString());
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        _client?.Dispose();
        _app = null;
        _client = null;
    }

    public void Dispose()
    {
        Serilog.Log.Logger = _originalStaticLogger!;
    }

    /// <summary>Writes one line through the host's DI logger into the current active file.</summary>
    private void SeedActiveLine(string marker)
    {
        _app!.Services
            .GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()
            .CreateLogger("LogsConfigEndpointsTests")
            .Log(Microsoft.Extensions.Logging.LogLevel.Information, default, marker, null,
                (state, _) => state);
    }

    private LogFormat ForwarderInnerFormat() =>
        _app!.Services.GetRequiredService<LogFormatForwardingSink>().Inner!.Format;

    private string ActivePath(LogFormat format) =>
        Path.Combine(Eling.Core.Scope.CentralLogDirectory.Resolve(),
            format == LogFormat.Jsonl ? "backend.jsonl" : "backend.log");

    private string ArchivePath(LogFormat format) =>
        Path.Combine(Eling.Core.Scope.CentralLogDirectory.Resolve(),
            $"backend-{DateTime.Today:yyyy-MM-dd}{(format == LogFormat.Jsonl ? ".jsonl" : ".log")}");

    private static string ReadShared(string path)
    {
        if (!File.Exists(path)) return string.Empty;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
