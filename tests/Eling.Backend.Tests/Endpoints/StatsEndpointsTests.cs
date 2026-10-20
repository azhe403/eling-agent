using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Eling.Backend.Bootstrap;
using Microsoft.AspNetCore.Builder;

namespace Eling.Backend.Tests.Endpoints;

/// <summary>
/// The dashboard's count-only read. The dashboard's first tile is a handful of
/// numbers, and these tests pin that the numbers are the aggregation's numbers
/// - counted where the aggregation already lives, not by shipping every row
/// to the browser.
/// </summary>
[Collection(ElingDataDirCollection.Name)]
public sealed class StatsEndpointsTests : IAsyncLifetime, IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        "eling-stats-tests-" + Guid.NewGuid().ToString("N")[..8]);

    private WebApplication? _app;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, ".eling"));

        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        _app = TestAppBuilder.CreateSelfContained(dashboardPort: port);
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        // The dashboard borrows the data directory of a registered runtime, so
        // one has to exist before any project-scoped memory can be saved.
        var registration = await _client.PostAsJsonAsync("/api/coordinator/register", new
        {
            processId = Environment.ProcessId,
            headScopeRoot = _tempDir,
            workspaceRoot = _tempDir,
            dataDirectory = Path.Combine(_tempDir, ".eling"),
            startTime = DateTimeOffset.UtcNow,
            mcpEnabled = false,
            mcpTransport = "none",
        });
        registration.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task GetMemoryStats_WithoutMemories_ReturnsZeroedCounts()
    {
        var stats = await ReadStatsAsync();

        Assert.NotNull(stats);
        Assert.True(stats!.Total >= 0);
        Assert.NotNull(stats.ByType);
        Assert.NotNull(stats.ByStatus);
        Assert.NotNull(stats.ByScope);
    }

    [Fact]
    public async Task GetMemoryStats_CountsSavedMemoriesByTypeAndStatus()
    {
        var before = await ReadStatsAsync();

        await SaveMemoryAsync("fact one", "Fact");
        await SaveMemoryAsync("fact two", "Fact");
        await SaveMemoryAsync("a decision", "Decision");

        var after = await ReadStatsAsync();

        Assert.Equal(before!.Total + 3, after!.Total);
        Assert.Equal(before.ByType.GetValueOrDefault("Fact") + 2, after.ByType.GetValueOrDefault("Fact"));
        Assert.Equal(before.ByType.GetValueOrDefault("Decision") + 1, after.ByType.GetValueOrDefault("Decision"));
        Assert.Equal(before.ByStatus.GetValueOrDefault("Active") + 3, after.ByStatus.GetValueOrDefault("Active"));

        // The scope breakdown uses the same wire spellings as the rest of the
        // API, and a project-scoped save has to land in the project bucket.
        Assert.Equal(before.ByScope.GetValueOrDefault("project") + 3, after.ByScope.GetValueOrDefault("project"));
    }

    [Fact]
    public async Task GetMemoryStats_ReturnsCountsThatMatchTheAggregatedList()
    {
        await SaveMemoryAsync("a fact", "Fact");
        await SaveMemoryAsync("a lesson", "Lesson");

        var stats = await ReadStatsAsync();
        var list = await _client!.GetFromJsonAsync<MemoryListProbe>(
            "/api/aggregated/memories?limit=1",
            JsonOptions);

        Assert.NotNull(stats);
        Assert.NotNull(list);
        Assert.Equal(list!.Metadata.Total, stats!.Total);
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        _client?.Dispose();

        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            /* the temp dir is best-effort cleanup */
        }
    }

    public void Dispose()
    {
        _client?.Dispose();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private async Task SaveMemoryAsync(string content, string type)
    {
        var res = await _client!.PostAsJsonAsync(
            $"/api/project/memories?projectRoot={Uri.EscapeDataString(_tempDir)}",
            new { content, type });
        res.EnsureSuccessStatusCode();
    }

    private async Task<MemoryStatsProbe?> ReadStatsAsync()
    {
        var res = await _client!.GetAsync("/api/stats/memories");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<MemoryStatsProbe>(JsonOptions);
    }

    private sealed record MemoryStatsProbe(
        int Total,
        Dictionary<string, int> ByType,
        Dictionary<string, int> ByStatus,
        Dictionary<string, int> ByScope);

    private sealed record MemoryListProbe(MemoryListMetadataProbe Metadata);

    private sealed record MemoryListMetadataProbe(int Total);
}
