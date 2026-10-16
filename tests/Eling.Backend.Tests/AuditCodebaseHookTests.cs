using System.Net;
using System.Text.Json;
using Eling.Backend.Bootstrap;
using Eling.Backend.Codebase;
using Eling.Backend.Endpoints;
using Eling.Backend.Mcp.Tools;
using Eling.Core.Audit;
using Eling.Core.Audit.Storage;
using Eling.Core.Codebase;
using Eling.Core.Memory;
using Eling.Core.Scope;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Eling.Backend.Tests;

/// <summary>
/// The codebase domain's half of the audit trail (design §8, amendment B):
/// explicit invocations record, the background watcher provably does not (it
/// calls <see cref="CodebaseIndexService.IndexAsync"/> directly and never
/// passes through a recorder — design §12). Tool tests run against isolated
/// temp stores; the two endpoint tests share one slim host whose audit
/// singletons are temp-local.
/// </summary>
public sealed class AuditCodebaseHookTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "eling-audit-codebase-" + Guid.NewGuid().ToString("N"));

    private string AuditRoot => Path.Combine(_root, "audit");
    private string DayFile(string trail) =>
        Path.Combine(AuditRoot, trail, DateTimeOffset.UtcNow.ToString("yyyy-MM-dd") + ".jsonl");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        // SqliteCodebaseIndex pools connections by default and its Dispose is
        // a no-op, so pooled handles keep codebase.db locked after the test.
        // Clearing the pools is test-only teardown hygiene, not production
        // behavior (cf. the audit index, which sets Pooling=false instead).
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    private async Task<BufferedAuditLogger> CreateLoggerAsync()
    {
        var store = new AuditFileStore(AuditRoot, TimeProvider.System);
        var index = new SqliteAuditIndex(AuditRoot, store);
        await index.InitializeAsync(CancellationToken.None);
        return new BufferedAuditLogger(store, index, new AuditOptions(), TimeProvider.System);
    }

    private CodebaseIndexService CreateService(string workspace)
    {
        Directory.CreateDirectory(workspace);
        return new CodebaseIndexService(
            workspace,
            new SqliteCodebaseIndex(Path.Combine(workspace, "codebase.db")));
    }

    private static string[] ReadLines(string trail, string root) =>
        File.Exists(Path.Combine(root, trail, DateTimeOffset.UtcNow.ToString("yyyy-MM-dd") + ".jsonl"))
            ? File.ReadAllLines(Path.Combine(root, trail, DateTimeOffset.UtcNow.ToString("yyyy-MM-dd") + ".jsonl"))
            : [];

    [Fact]
    public async Task IndexTool_ExplicitInvocation_RecordsOneChangeEntry()
    {
        var workspace = Path.Combine(_root, "ws-index");
        var svc = CreateService(workspace);
        await using var logger = await CreateLoggerAsync();
        var tool = new CodebaseIndexTool(svc, logger: null, notifier: null, audit: logger);

        using (AuditActorContext.Begin(new AuditActor("mcp:eling_dev", "mcp_stdio")))
        {
            await tool.IndexAsync(full: true, cancellationToken: CancellationToken.None);
        }

        var entry = Assert.Single(ReadLines("changes", AuditRoot));
        Assert.Contains("\"action\":\"codebase_index\"", entry, StringComparison.Ordinal);
        Assert.Contains("\"trail\":\"change\"", entry, StringComparison.Ordinal);
        Assert.Contains("\"category\":\"codebase\"", entry, StringComparison.Ordinal);
        Assert.Contains("\"actor\":\"mcp:eling_dev\"", entry, StringComparison.Ordinal);
        Assert.Contains("\"target\":\"ws-index\"", entry, StringComparison.Ordinal);
        Assert.Contains("\"durationMs\":", entry, StringComparison.Ordinal);
        Assert.DoesNotContain("\"metadata\"", entry, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServiceDirectCall_WatcherPath_RecordsNothing()
    {
        // Exactly what CodebaseWatcherService does on every file change and on
        // every startup pass: call the service, bypassing every recorder.
        var workspace = Path.Combine(_root, "ws-watcher");
        var svc = CreateService(workspace);
        await using var logger = await CreateLoggerAsync();
        _ = new CodebaseIndexTool(svc, logger: null, notifier: null, audit: logger);

        using (AuditActorContext.Begin(new AuditActor("mcp:eling_dev", "mcp_stdio")))
        {
            await svc.IndexAsync(full: true, ct: CancellationToken.None);
        }

        Assert.False(File.Exists(DayFile("changes")));
        Assert.False(File.Exists(DayFile("access")));
    }

    [Fact]
    public async Task SearchTool_CompletedSearch_RecordsAccessEntryWithQueryAndCount()
    {
        var workspace = Path.Combine(_root, "ws-search");
        var svc = CreateService(workspace);
        var token = "auditprobe" + Guid.NewGuid().ToString("N")[..8];
        await File.WriteAllTextAsync(
            Path.Combine(workspace, "probe.cs"),
            $"// {token} unique marker\nclass Probe {{ }}");
        await svc.IndexAsync(full: true, ct: CancellationToken.None);

        var logger = await CreateLoggerAsync();
        var tool = new CodebaseSearchTool(svc, registry: null, logger: null, audit: logger);

        using (AuditActorContext.Begin(new AuditActor("mcp:eling_dev", "mcp_stdio")))
        {
            await tool.SearchAsync(token, cancellationToken: CancellationToken.None);
        }

        // Access entries are fire-and-forget: disposing drains the channel,
        // so the read below is deterministic rather than a race with the
        // background flusher. Single disposal — the logger is not await-using.
        await logger.DisposeAsync();

        var entry = Assert.Single(ReadLines("access", AuditRoot));
        Assert.Contains("\"action\":\"codebase_search\"", entry, StringComparison.Ordinal);
        Assert.Contains($"\"query\":\"{token}\"", entry, StringComparison.Ordinal);
        Assert.Contains("\"actor\":\"mcp:eling_dev\"", entry, StringComparison.Ordinal);
        Assert.DoesNotContain("\"metadata\"", entry, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(entry);
        Assert.True(doc.RootElement.GetProperty("resultCount").GetInt32() >= 1);
    }

    [Fact]
    public async Task SearchTool_EmptyQuery_RecordsNothing()
    {
        var workspace = Path.Combine(_root, "ws-emptyq");
        var svc = CreateService(workspace);
        await using var logger = await CreateLoggerAsync();
        var tool = new CodebaseSearchTool(svc, registry: null, logger: null, audit: logger);

        using (AuditActorContext.Begin(new AuditActor("mcp:eling_dev", "mcp_stdio")))
        {
            await tool.SearchAsync("", cancellationToken: CancellationToken.None);
        }

        Assert.False(File.Exists(DayFile("access")));
    }

    [Fact]
    public async Task StatusTool_RecordsOneAccessEntry()
    {
        var workspace = Path.Combine(_root, "ws-status");
        var svc = CreateService(workspace);
        var logger = await CreateLoggerAsync();
        var tool = new CodebaseStatusTool(svc, watcher: null, logger: null, audit: logger);

        using (AuditActorContext.Begin(new AuditActor("mcp:eling_dev", "mcp_stdio")))
        {
            await tool.GetStatusAsync(CancellationToken.None);
        }

        // Drain before reading: access entries are fire-and-forget (see above).
        await logger.DisposeAsync();

        var entry = Assert.Single(ReadLines("access", AuditRoot));
        Assert.Contains("\"action\":\"codebase_status\"", entry, StringComparison.Ordinal);
        Assert.Contains("\"target\":\"ws-status\"", entry, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RebuildEndpoint_AcceptedRebuild_RecordsOneChangeEntryAsDashboard()
    {
        var workspace = Path.Combine(_root, "ws-rebuild");
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace, "probe.cs"), "// rebuild probe");
        var auditRoot = Path.Combine(_root, "audit-ep");

        var store = new AuditFileStore(auditRoot, TimeProvider.System);
        var index = new SqliteAuditIndex(auditRoot, store);
        await index.InitializeAsync(CancellationToken.None);
        await using var logger = new BufferedAuditLogger(store, index, new AuditOptions(), TimeProvider.System);

        var broadcaster = new CodebaseRebuildBroadcaster();
        var local = new CodebaseIndexService(
            workspace,
            new SqliteCodebaseIndex(Path.Combine(workspace, "codebase.db")));
        using var registry = new RuntimeRegistry(
            NullLogger<RuntimeRegistry>.Instance,
            UserScope.Resolve(Path.Combine(_root, "user-scope")));

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(local);
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton(broadcaster);
        builder.Services.AddSingleton(new MemoryChangeBroadcaster());
        builder.Services.AddSingleton<IMemoryChangeNotifier>(NullMemoryChangeNotifier.Instance);
        builder.Services.AddSingleton<CodebaseWatcherService>();
        builder.Services.AddSingleton<Eling.Backend.Services.ProcessMetricsTracker>();
        builder.Services.AddSingleton<Eling.Backend.Services.RuntimeMetricsSampler>();
        builder.Services.AddSingleton(sp => new Eling.Backend.Services.RuntimeMetricsBroadcaster(
            sp.GetRequiredService<Eling.Backend.Services.RuntimeMetricsSampler>(),
            sp.GetRequiredService<ILogger<Eling.Backend.Services.RuntimeMetricsBroadcaster>>()));
        builder.Services.AddSingleton<CodebaseRebuildJobRunner>(sp =>
            new CodebaseRebuildJobRunner(
                local,
                broadcaster,
                NullMemoryChangeNotifier.Instance,
                NullLogger<CodebaseRebuildJobRunner>.Instance,
                sp.GetService<IHostApplicationLifetime>(),
                reindex: (root, full, _) =>
                    Task.FromResult(new CodebaseIndexResult(2, 5, 1, 0, "stub.db"))));
        builder.Services.AddSingleton<IAuditLogger>(logger);

        await using var app = builder.Build();
        app.MapCodebaseRoutes();
        app.MapSseEvents();
        await app.StartAsync();
        using var client = app.GetTestClient();

        try
        {
            var response = await client.PostAsync("/api/codebase/rebuild-index", content: null);

            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var entry = Assert.Single(ReadLines("changes", auditRoot));
            Assert.Contains("\"action\":\"codebase_index\"", entry, StringComparison.Ordinal);
            Assert.Contains("\"actor\":\"dashboard\"", entry, StringComparison.Ordinal);
            Assert.Contains("\"target\":\"ws-rebuild\"", entry, StringComparison.Ordinal);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task SearchEndpoint_CompletedSearch_RecordsAccessEntryAsDashboard()
    {
        var workspace = Path.Combine(_root, "ws-epsearch");
        Directory.CreateDirectory(workspace);
        var token = "epprobe" + Guid.NewGuid().ToString("N")[..8];
        await File.WriteAllTextAsync(
            Path.Combine(workspace, "probe.cs"),
            $"// {token} unique marker\nclass Probe {{ }}");
        var auditRoot = Path.Combine(_root, "audit-epsearch");

        var store = new AuditFileStore(auditRoot, TimeProvider.System);
        var index = new SqliteAuditIndex(auditRoot, store);
        await index.InitializeAsync(CancellationToken.None);
        var logger = new BufferedAuditLogger(store, index, new AuditOptions(), TimeProvider.System);

        // Indexed through the service directly — the watcher path — so the
        // setup itself records nothing; only the endpoint call below may.
        var local = new CodebaseIndexService(
            workspace,
            new SqliteCodebaseIndex(Path.Combine(workspace, "codebase.db")));
        await local.IndexAsync(full: true, ct: CancellationToken.None);

        var broadcaster = new CodebaseRebuildBroadcaster();
        using var registry = new RuntimeRegistry(
            NullLogger<RuntimeRegistry>.Instance,
            UserScope.Resolve(Path.Combine(_root, "user-scope-search")));

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(local);
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton(broadcaster);
        builder.Services.AddSingleton(new MemoryChangeBroadcaster());
        builder.Services.AddSingleton<IMemoryChangeNotifier>(NullMemoryChangeNotifier.Instance);
        builder.Services.AddSingleton<CodebaseWatcherService>();
        builder.Services.AddSingleton<Eling.Backend.Services.ProcessMetricsTracker>();
        builder.Services.AddSingleton<Eling.Backend.Services.RuntimeMetricsSampler>();
        builder.Services.AddSingleton(sp => new Eling.Backend.Services.RuntimeMetricsBroadcaster(
            sp.GetRequiredService<Eling.Backend.Services.RuntimeMetricsSampler>(),
            sp.GetRequiredService<ILogger<Eling.Backend.Services.RuntimeMetricsBroadcaster>>()));
        builder.Services.AddSingleton<CodebaseRebuildJobRunner>(sp =>
            new CodebaseRebuildJobRunner(
                local,
                broadcaster,
                NullMemoryChangeNotifier.Instance,
                NullLogger<CodebaseRebuildJobRunner>.Instance,
                sp.GetService<IHostApplicationLifetime>(),
                reindex: (root, full, _) =>
                    Task.FromResult(new CodebaseIndexResult(2, 5, 1, 0, "stub.db"))));
        builder.Services.AddSingleton<IAuditLogger>(logger);

        await using var app = builder.Build();
        app.MapCodebaseRoutes();
        app.MapSseEvents();
        await app.StartAsync();
        using var client = app.GetTestClient();

        try
        {
            var response = await client.GetAsync(
                $"/api/codebase/search?q={Uri.EscapeDataString(token)}&scope=project");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            // Drain before reading: the searched entry is access-trail,
            // hence fire-and-forget.
            await logger.DisposeAsync();
            var entry = Assert.Single(ReadLines("access", auditRoot));
            Assert.Contains("\"action\":\"codebase_search\"", entry, StringComparison.Ordinal);
            Assert.Contains($"\"query\":\"{token}\"", entry, StringComparison.Ordinal);
            Assert.Contains("\"actor\":\"dashboard\"", entry, StringComparison.Ordinal);
            using var doc = JsonDocument.Parse(entry);
            Assert.True(doc.RootElement.GetProperty("resultCount").GetInt32() >= 1);
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
