using System.Globalization;
using System.Net;
using System.Text.Json;
using Eling.Backend.Endpoints;
using Eling.Core.Audit;
using Eling.Core.Audit.Storage;
using Eling.Core.Memory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Eling.Backend.Tests;

/// <summary>
/// The audit read API (design §9): two endpoints over the index the writer
/// batches into, a mandatory time window, and the canonical kebab labels on
/// the wire (amendment C1). Entries are seeded through the real BufferedAuditLogger
/// so the read path is exercised against what the write path actually produces.
/// </summary>
public sealed class AuditEndpointTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "eling-audit-ep-" + Guid.NewGuid().ToString("N"));

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var store = new AuditFileStore(_root, TimeProvider.System);
        var index = new SqliteAuditIndex(_root, store);
        await index.InitializeAsync(CancellationToken.None);
        await using var logger = new BufferedAuditLogger(store, index, new AuditOptions(), TimeProvider.System);

        var t0 = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
        using (AuditActorContext.Begin(new AuditActor("mcp:eling_dev", "mcp_stdio")))
        {
            await logger.RecordAsync(new AuditEvent
            {
                Timestamp = t0,
                Trail = AuditTrail.Change,
                Category = AuditCategory.Memory,
                Action = "memory_save",
                Scope = MemoryScopeKind.Project,
                ProjectRoot = "eling-agent",
                Target = "01m0000000000000000000000",
                Metadata = new AuditMetadata { Content = "one", Tags = ["ep"], Status = "active" },
            });
            await logger.RecordAsync(new AuditEvent
            {
                Timestamp = t0.AddMinutes(5),
                Trail = AuditTrail.Change,
                Category = AuditCategory.Memory,
                Action = "memory_delete",
                Scope = MemoryScopeKind.Project,
                Target = "01m0000000000000000000001",
                PreviousMetadata = new AuditMetadata { Content = "gone" },
            });
            await logger.RecordAsync(new AuditEvent
            {
                Timestamp = t0.AddMinutes(10),
                Trail = AuditTrail.Access,
                Category = AuditCategory.Memory,
                Action = "memory_search",
                Actor = "dashboard",
                Source = "dashboard_web",
                Query = "audit endpoint",
                ResultCount = 2,
            });
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton<IAuditIndex>(index);
        builder.WebHost.UseTestServer();
        _app = builder.Build();
        _app.MapAuditRoutes();
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Changes_ReturnsWhatTheLoggerWrote_WithCanonicalLabels()
    {
        var from = new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero).ToString("O", CultureInfo.InvariantCulture);
        var to = new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero).ToString("O", CultureInfo.InvariantCulture);

        var res = await _client.GetAsync($"/api/audit/changes?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        // Newest first, canonical kebab labels on the wire.
        Assert.Equal("memory_delete", items[0].GetProperty("action").GetString());
        Assert.Equal("memory", items[0].GetProperty("category").GetString());
        Assert.Equal("change", items[0].GetProperty("trail").GetString());
        Assert.Equal("mcp:eling_dev", items[0].GetProperty("actor").GetString());
        Assert.Equal("project", items[0].GetProperty("scope").GetString());
        Assert.Equal("gone", items[0].GetProperty("previousMetadata").GetProperty("content").GetString());
    }

    [Fact]
    public async Task Access_ReturnsAccessEntries_WithReadDetail()
    {
        var from = new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero).ToString("O", CultureInfo.InvariantCulture);
        var to = new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero).ToString("O", CultureInfo.InvariantCulture);

        var res = await _client.GetAsync($"/api/audit/access?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var items = doc.RootElement.GetProperty("items");
        var entry = Assert.Single(items.EnumerateArray());
        Assert.Equal("memory_search", entry.GetProperty("action").GetString());
        Assert.Equal("audit endpoint", entry.GetProperty("query").GetString());
        Assert.Equal(2, entry.GetProperty("resultCount").GetInt32());
    }

    [Fact]
    public async Task MissingTimeWindow_IsRejected()
    {
        var res = await _client.GetAsync("/api/audit/changes?limit=10");

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
