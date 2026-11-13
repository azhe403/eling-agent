using System.Net;
using System.Text.Json;
using Eling.Backend.Bootstrap;
using Eling.Core.Git;
using Eling.Core.CommitLease;
using Eling.Core.Scope;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Eling.Backend.Tests;

/// <summary>
/// Integration tests for the owner-mode commit-lease HTTP surface:
/// <c>GET /api/commit-lease/active</c> (snapshot), <c>GET /api/events/commit-lease</c>
/// (SSE; exercised by <see cref="CommitLeaseBroadcasterTests"/>) and
/// <c>POST /api/commit-lease/{leaseId}/release</c> (force-release). The host is
/// built through the same <see cref="TestAppBuilder"/> path the production
/// dashboard owner uses — a free port means owner mode, so the routes are
/// mapped exactly as in production. Each test class instance gets a fresh host
/// rooted at a scratch workspace and an isolated <c>commit-leases.json</c>.
/// </summary>
[Collection(ElingDataDirCollection.Name)]
public sealed class CommitLeaseEndpointTests : IAsyncLifetime, IDisposable
{
    private readonly string _root;
    private readonly string _workspace;
    private readonly ITestOutputHelper _output;
    private string _checkout = "";
    private WebApplication? _app;
    private HttpClient? _client;

    public CommitLeaseEndpointTests(ITestOutputHelper output)
    {
        _output = output;
        _root = Path.Combine(Path.GetTempPath(), "eling-commit-lease-ep-" + Guid.NewGuid().ToString("N")[..8]);
        _workspace = Path.Combine(_root, "workspace");
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(Path.Combine(_workspace, ".eling"));
        Directory.CreateDirectory(Path.Combine(_root, "user_scope"));
        Directory.CreateDirectory(Path.Combine(_root, "other-checkout"));
        Environment.SetEnvironmentVariable("ELING_AGENT_DATA_DIR", Path.Combine(_root, "agent_data"));
        Environment.SetEnvironmentVariable("ELING_DATA_DIR", Path.Combine(_root, "data"));

        var host = await BuildHostAsync(_workspace);
        _app = host.App;
        _client = host.Client;
        _checkout = host.Checkout;
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        _client = null;
        if (_app is not null)
        {
            try { await _app.StopAsync(); }
            catch (Exception ex) { _output.WriteLine($"StopAsync failed during teardown: {ex.Message}"); }

            try { await _app.DisposeAsync(); }
            catch (Exception ex) { _output.WriteLine($"DisposeAsync failed during teardown: {ex.Message}"); }
            _app = null;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (Exception ex) { _output.WriteLine($"Scratch workspace cleanup failed: {ex.Message}"); }
        }

        GC.SuppressFinalize(this);
    }

    private sealed record HostHandle(
        WebApplication App,
        HttpClient Client,
        string Checkout);

    private async Task<HostHandle> BuildHostAsync(string workspace)
    {
        var chain = ScopeChain.Discover(workspace);
        var userScope = UserScope.Resolve(Path.Combine(_root, "user_scope"));
        var context = new ProjectContext(chain, userScope, Path.Combine(workspace, ".eling"), IsUserHome: false);
        // The same resolution the tools and the endpoint apply: a path outside
        // every git repository keys by itself, so the test workspace is its own
        // working copy.
        var checkout = GitRepositoryResolver.ResolveWorkingCopyRoot(context.Chain.Cwd) ?? context.Chain.Cwd;
        var shared = AppServices.Create(context);

        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var app = TestAppBuilder.Create(shared, context, port, shared.LoggerFactory);
        await app.StartAsync();
        var client = new HttpClient(new EnvelopeTestHandler { InnerHandler = new HttpClientHandler() })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}")
        };
        return new HostHandle(app, client, checkout);
    }

    private async Task<CommitLeaseRecord> SeedAsync(
        string sessionId,
        string? branch = "main",
        DateTimeOffset? lastHeartbeat = null,
        int ttlSeconds = 300,
        string? checkout = null)
    {
        var store = _app!.Services.GetRequiredService<ICommitLeaseStore>();
        var now = DateTimeOffset.UtcNow;
        var lease = new CommitLeaseRecord {
            LeaseId =  Ulid.NewUlid().ToString(),
            SessionId =  sessionId,
            Kind =  CommitLeaseKind.Commit,
            Checkout =  checkout ?? _checkout,
            Branch =  branch,
            Since =  now,
            LastHeartbeat =  lastHeartbeat ?? now,
            TtlSeconds =  ttlSeconds,
            Paths =  ["src/a.cs"],
            Intent =  "test lease"};
        var conflict = await store.TryLeaseAsync(lease);
        Assert.Null(conflict);
        return lease;
    }

    private HttpClient Client => _client ?? throw new InvalidOperationException("Host failed to initialize.");

    [Fact]
    public async Task GetActive_ReturnsSeededLease_WithProjectedFields()
    {
        var lease = await SeedAsync("ses_test_2_000000000000000000000");

        var res = await Client.GetAsync("/api/commit-lease/active");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal(_checkout, root.GetProperty("checkout").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("branch").ValueKind);

        var row = Assert.Single(root.GetProperty("commit_leases").EnumerateArray());
        Assert.Equal(lease.LeaseId, row.GetProperty("lease_id").GetString());
        Assert.Equal("ses_test_2_000000000000000000000", row.GetProperty("session_id").GetString());
        Assert.Equal("main", row.GetProperty("branch").GetString());
        Assert.Equal(lease.Since, row.GetProperty("since").GetDateTimeOffset());
        Assert.Equal(lease.LastHeartbeat, row.GetProperty("last_heartbeat").GetDateTimeOffset());
        Assert.True(row.GetProperty("lease_age_seconds").GetDouble() >= 0);
        Assert.Equal(300, row.GetProperty("ttl_seconds").GetInt32());
        Assert.Equal("src/a.cs", Assert.Single(row.GetProperty("paths").EnumerateArray()).GetString());
        Assert.Equal("test lease", row.GetProperty("intent").GetString());

        Assert.Empty(root.GetProperty("recently_released").EnumerateArray());
    }

    [Fact]
    public async Task GetActive_BranchFilter_NarrowsToThatBranch()
    {
        await SeedAsync("ses_test_2_000000000000000000000", branch: "main");
        await SeedAsync("ses_test_2_000000000000000000000", branch: "feature/x");

        var res = await Client.GetAsync("/api/commit-lease/active?branch=main");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var row = Assert.Single(doc.RootElement.GetProperty("commit_leases").EnumerateArray());
        Assert.Equal("ses_test_2_000000000000000000000", row.GetProperty("session_id").GetString());
        Assert.Equal("main", doc.RootElement.GetProperty("branch").GetString());
    }

    [Fact]
    public async Task GetActive_StaleLease_IsAbsent_AndReportedAsStaleInRecentlyReleased()
    {
        var stale = await SeedAsync("ses_stale_000000000000000000000000", lastHeartbeat: DateTimeOffset.UtcNow.AddSeconds(-901), ttlSeconds: 300);

        var res = await Client.GetAsync("/api/commit-lease/active");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Empty(doc.RootElement.GetProperty("commit_leases").EnumerateArray());
        var released = Assert.Single(doc.RootElement.GetProperty("recently_released").EnumerateArray());
        Assert.Equal(stale.LeaseId, released.GetProperty("lease_id").GetString());
        Assert.Equal("stale", released.GetProperty("release_reason").GetString());
        // Recently-released rows keep their shape: no last_heartbeat there.
        Assert.False(released.TryGetProperty("last_heartbeat", out _));
    }

    [Fact]
    public async Task GetActive_OtherCheckoutsLeases_AreAbsent()
    {
        await SeedAsync("ses_own_000000000000000000000000");
        await SeedAsync("session-foreign", checkout: Path.Combine(_root, "other-checkout"));

        var res = await Client.GetAsync("/api/commit-lease/active");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var row = Assert.Single(doc.RootElement.GetProperty("commit_leases").EnumerateArray());
        Assert.Equal("ses_own_000000000000000000000000", row.GetProperty("session_id").GetString());
    }

    [Fact]
    public async Task GetActive_scopeAll_ReturnsLeasesAcrossCheckouts()
    {
        var foreignRoot = Path.Combine(_root, "other-checkout");
        await SeedAsync("ses_own_000000000000000000000000");
        await SeedAsync("session-foreign", checkout: foreignRoot);

        var res = await Client.GetAsync("/api/commit-lease/active?scope=all");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        // A snapshot that spans checkouts carries no single checkout...
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("checkout").ValueKind);
        var rows = doc.RootElement.GetProperty("commit_leases").EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, row => row.GetProperty("session_id").GetString() == "ses_own_000000000000000000000000"
            && string.Equals(row.GetProperty("checkout").GetString(), _checkout, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(rows, row => row.GetProperty("session_id").GetString() == "session-foreign"
            && string.Equals(row.GetProperty("checkout").GetString(), foreignRoot, StringComparison.OrdinalIgnoreCase));

        // ...while the default (no scope) stays scoped to this checkout.
        var scoped = await Client.GetAsync("/api/commit-lease/active");
        using var scopedDoc = JsonDocument.Parse(await scoped.Content.ReadAsStringAsync());
        var scopedRow = Assert.Single(scopedDoc.RootElement.GetProperty("commit_leases").EnumerateArray());
        Assert.Equal("ses_own_000000000000000000000000", scopedRow.GetProperty("session_id").GetString());
        Assert.Equal(_checkout, scopedDoc.RootElement.GetProperty("checkout").GetString());
    }

    [Fact]
    public async Task NonOwnerHost_DoesNotMapCommitLeaseEndpoints()
    {
        // A peer eling holds the dashboard port; this process therefore serves
        // no HTTP at all (HttpLoop binds nothing in non-owner builds and never
        // maps the dashboard routes). Its route table must carry no
        // commit-lease endpoints either - a request would hit an empty
        // pipeline rather than an owner handler.
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var occupiedPort = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

        var chain = ScopeChain.Discover(_workspace);
        var userScope = UserScope.Resolve(Path.Combine(_root, "user_scope"));
        var context = new ProjectContext(chain, userScope, Path.Combine(_workspace, ".eling"), IsUserHome: false);
        var shared = AppServices.Create(context);
        await using var app = TestAppBuilder.Create(shared, context, occupiedPort, shared.LoggerFactory);

        var mapped = app.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .Where(pattern => pattern is not null
                && pattern.StartsWith("/api/commit-lease", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(mapped);
    }

    [Fact]
    public async Task GetCommitLeaseEvents_StreamsConnectedThenTheInitialSnapshot()
    {
        var own = await SeedAsync("ses_sse_own_000000000000000000000000");
        var foreign = await SeedAsync("ses_sse_foreign_00000000000000000000000",
            checkout: Path.Combine(_root, "other-checkout"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/events/commit-lease");
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.EnsureSuccessStatusCode();
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        await using var body = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(body);

        Assert.Equal("data: connected", await reader.ReadLineAsync(cts.Token));
        Assert.Equal("", await reader.ReadLineAsync(cts.Token)); // blank separator

        var frame = await reader.ReadLineAsync(cts.Token)
            ?? throw new InvalidOperationException("Stream ended before the initial snapshot.");
        Assert.StartsWith("data: ", frame);
        // The frame is machine-global: no single checkout, rows from every
        // checkout that holds a live lease.
        using var doc = JsonDocument.Parse(frame["data: ".Length..]);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("checkout").ValueKind);
        var rows = doc.RootElement.GetProperty("commit_leases").EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, row => row.GetProperty("lease_id").GetString() == own.LeaseId
            && row.GetProperty("session_id").GetString() == "ses_sse_own_000000000000000000000000"
            && row.GetProperty("last_heartbeat").GetDateTimeOffset() == own.LastHeartbeat);
        Assert.Contains(rows, row => row.GetProperty("lease_id").GetString() == foreign.LeaseId
            && row.GetProperty("session_id").GetString() == "ses_sse_foreign_00000000000000000000000"
            && row.GetProperty("last_heartbeat").GetDateTimeOffset() == foreign.LastHeartbeat);
    }

    [Fact]
    public async Task PostRelease_ForceReleasesForeignOwnedLease_AndReportsItAsForced()
    {
        var foreign = await SeedAsync("ses_foreign_0000000000000000000000000");

        var res = await Client.PostAsync($"/api/commit-lease/{foreign.LeaseId}/release", content: null);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Empty(doc.RootElement.GetProperty("commit_leases").EnumerateArray());
        var released = Assert.Single(doc.RootElement.GetProperty("recently_released").EnumerateArray());
        Assert.Equal(foreign.LeaseId, released.GetProperty("lease_id").GetString());
        Assert.Equal("forced", released.GetProperty("release_reason").GetString());
        Assert.Equal("ses_foreign_0000000000000000000000000", released.GetProperty("session_id").GetString());

        // The same view after the fact: the lease is gone from the active set
        // and present in the recently-released history with reason "forced".
        var get = await Client.GetAsync("/api/commit-lease/active");
        using var getDoc = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        Assert.Empty(getDoc.RootElement.GetProperty("commit_leases").EnumerateArray());
        var history = Assert.Single(getDoc.RootElement.GetProperty("recently_released").EnumerateArray());
        Assert.Equal("forced", history.GetProperty("release_reason").GetString());
    }

    [Fact]
    public async Task PostRelease_CheckoutQueryParam_ReleasesAForeignCheckoutLiveLease()
    {
        // The dashboard's all-workspaces view can see a lease held in
        // another checkout; an explicit ?checkout= lets a human force it
        // loose from there.
        var foreignRoot = Path.Combine(_root, "other-checkout");
        var foreign = await SeedAsync("ses_foreign_0000000000000000000000000", checkout: foreignRoot);

        var res = await Client.PostAsync(
            $"/api/commit-lease/{foreign.LeaseId}/release?checkout={Uri.EscapeDataString(foreignRoot)}",
            content: null);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        // The response is the same machine-global frame the SSE channel
        // pushes; checkout is null.
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("checkout").ValueKind);
        var released = Assert.Single(doc.RootElement.GetProperty("recently_released").EnumerateArray());
        Assert.Equal(foreign.LeaseId, released.GetProperty("lease_id").GetString());
        Assert.Equal("forced", released.GetProperty("release_reason").GetString());
        Assert.Equal(foreignRoot, released.GetProperty("checkout").GetString());

        // Gone from the foreign checkout's live set, recorded in its history.
        var store = _app!.Services.GetRequiredService<ICommitLeaseStore>();
        Assert.Empty(await store.ListActiveAsync(foreignRoot, branch: null));
        var foreignHistory = await store.ListRecentlyReleasedAsync(foreignRoot, limit: 20);
        Assert.Contains(foreignHistory, entry => entry.LeaseId == foreign.LeaseId
            && entry.ReleaseReason == "forced");
    }

    [Fact]
    public async Task PostRelease_ForeignCheckoutLease_Returns404_AndSurvivesUntouched()
    {
        // The lease id is real and live, but belongs to another checkout: the
        // host must refuse rather than reach across the working-tree boundary.
        var foreign = await SeedAsync("ses_foreign_0000000000000000000000000", checkout: Path.Combine(_root, "other-checkout"));

        var res = await Client.PostAsync($"/api/commit-lease/{foreign.LeaseId}/release", content: null);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);

        // This host serves only its own checkout, so its snapshot is empty ...
        var get = await Client.GetAsync("/api/commit-lease/active");
        using var doc = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        Assert.Empty(doc.RootElement.GetProperty("commit_leases").EnumerateArray());
        Assert.Empty(doc.RootElement.GetProperty("recently_released").EnumerateArray());

        // ... and the lease still exists, unreleased, on its own checkout.
        var store = _app!.Services.GetRequiredService<ICommitLeaseStore>();
        var foreignActive = await store.ListActiveAsync(Path.Combine(_root, "other-checkout"), branch: null);
        var survivor = Assert.Single(foreignActive);
        Assert.Equal(foreign.LeaseId, survivor.LeaseId);
        Assert.Equal("ses_foreign_0000000000000000000000000", survivor.SessionId);
    }

    [Fact]
    public async Task PostRelease_WritesAuditEntry_ChangeTrail_WithForcedMarker()
    {
        var lease = await SeedAsync("ses_foreign_0000000000000000000000000");

        var res = await Client.PostAsync($"/api/commit-lease/{lease.LeaseId}/release", content: null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        // Change-trail entries are flushed to disk before RecordAsync returns
        // (amendment A6 - "flushed before return"), so the day file is already
        // final when this line reads it. ELING_DATA_DIR is isolated to this
        // test's temp root, and the host wires the real BufferedAuditLogger.
        var dayFile = Path.Combine(
            _root, "data", "audit", "changes",
            DateTimeOffset.UtcNow.ToString("yyyy-MM-dd") + ".jsonl");
        Assert.True(File.Exists(dayFile), $"Expected an audit change day file at {dayFile}.");
        var entry = Assert.Single(
            await File.ReadAllLinesAsync(dayFile),
            l => l.Contains("\"commit_lease_force_release\"", StringComparison.Ordinal));

        using var doc = JsonDocument.Parse(entry);
        var root = doc.RootElement;
        Assert.Equal("commit_lease_force_release", root.GetProperty("action").GetString());
        Assert.Equal("change", root.GetProperty("trail").GetString());
        Assert.Equal("runtime", root.GetProperty("category").GetString());
        Assert.Equal("dashboard", root.GetProperty("actor").GetString());
        Assert.Equal(lease.LeaseId, root.GetProperty("target").GetString());
        Assert.Equal(_checkout, root.GetProperty("projectRoot").GetString());
        Assert.Equal("success", root.GetProperty("outcome").GetString());

        var metadata = root.GetProperty("metadata");
        Assert.Equal("forced", metadata.GetProperty("status").GetString());
        Assert.Contains("commit-lease", metadata.GetProperty("tags").EnumerateArray().Select(t => t.GetString()!));
        Assert.Contains("forced", metadata.GetProperty("tags").EnumerateArray().Select(t => t.GetString()!));

        // The full forced payload rides in the metadata content string.
        using var payload = JsonDocument.Parse(metadata.GetProperty("content").GetString()!);
        Assert.Equal(lease.LeaseId, payload.RootElement.GetProperty("lease_id").GetString());
        Assert.Equal("ses_foreign_0000000000000000000000000", payload.RootElement.GetProperty("session_id").GetString());
        Assert.Equal(_checkout, payload.RootElement.GetProperty("checkout").GetString());
        Assert.Equal("main", payload.RootElement.GetProperty("branch").GetString());
        Assert.Equal("src/a.cs", Assert.Single(payload.RootElement.GetProperty("paths").EnumerateArray()).GetString());
        Assert.Equal("test lease", payload.RootElement.GetProperty("intent").GetString());
        Assert.Equal("forced", payload.RootElement.GetProperty("release_reason").GetString());
    }

    [Fact]
    public async Task PostRelease_UnknownLease_Returns404_AndReleasesNothing()
    {
        var res = await Client.PostAsync("/api/commit-lease/does-not-exist/release", content: null);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task PostRelease_AlreadyReleasedLease_Returns404_AndReleasesNothing()
    {
        var lease = await SeedAsync("ses_once_0000000000000000000000000");

        var first = await Client.PostAsync($"/api/commit-lease/{lease.LeaseId}/release", content: null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await Client.PostAsync($"/api/commit-lease/{lease.LeaseId}/release", content: null);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);

        var get = await Client.GetAsync("/api/commit-lease/active");
        using var doc = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        Assert.Empty(doc.RootElement.GetProperty("commit_leases").EnumerateArray());
        var released = Assert.Single(doc.RootElement.GetProperty("recently_released").EnumerateArray());
        Assert.Equal(lease.LeaseId, released.GetProperty("lease_id").GetString());
        Assert.Equal("forced", released.GetProperty("release_reason").GetString());
    }
}
