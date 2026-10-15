using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Eling.Backend.Bootstrap;
using Microsoft.AspNetCore.Builder;

namespace Eling.Backend.Tests;

/// <summary>
/// The project-memory list endpoint is the dashboard's read path: one call per
/// selection, merged over every requested workspace and — on request — its full
/// .eling chain, so the caller never stitches fragments together.
///
/// Every fixture here is deliberately unregistered: no runtime is alive for any
/// of these roots, and the reads still work, which is the disk fallback in
/// <c>RuntimeRegistry.TryResolveMemoryServiceByScopeRoot</c> doing its job.
/// The unregistered ancestor is the normal case, not an exotic one.
/// </summary>
[Collection(ElingDataDirCollection.Name)]
public class ProjectMemoryChainEndpointTests : IAsyncLifetime, IDisposable
{
    private readonly string _tempDir;
    private readonly string _parentRoot;
    private readonly string _childARoot;
    private readonly string _childBRoot;
    private WebApplication? _app;
    private HttpClient? _client;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ProjectMemoryChainEndpointTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "eling-chain-" + Guid.NewGuid().ToString("N")[..8]);
        _parentRoot = Path.Combine(_tempDir, "parent");
        _childARoot = Path.Combine(_parentRoot, "child-a");
        _childBRoot = Path.Combine(_parentRoot, "child-b");
    }

    public async Task InitializeAsync()
    {
        foreach (var root in new[] { _parentRoot, _childARoot, _childBRoot })
        {
            Directory.CreateDirectory(Path.Combine(root, ".eling"));
        }

        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        _app = TestAppBuilder.CreateSelfContained(dashboardPort: port);
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        _client = null;
        if (_app is not null)
        {
            try
            {
                await _app.StopAsync();
            }
            catch
            {
                /* ignore */
            }
            try
            {
                await _app.DisposeAsync();
            }
            catch
            {
                /* ignore */
            }
            _app = null;
        }
    }

    public void Dispose()
    {
        TryDeleteDirectory(_tempDir);
        GC.SuppressFinalize(this);
    }

    private HttpClient EnsureClient() =>
        _client ?? throw new InvalidOperationException("Test host failed to initialize.");

    private async Task<string> CreateMemoryAsync(string projectRoot, string content, string? tier = null, string? type = null)
    {
        var url = $"/api/project/memories?projectRoot={Uri.EscapeDataString(projectRoot)}";
        if (tier is not null) url += $"&tier={Uri.EscapeDataString(tier)}";
        var response = await EnsureClient().PostAsJsonAsync(url, new { content, type });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        return body.GetProperty("id").GetString()!;
    }

    private async Task<string> CreateGlobalMemoryAsync(string content, string? type = null)
    {
        var response = await EnsureClient().PostAsJsonAsync("/api/global/memories", new { content, type });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        return body.GetProperty("id").GetString()!;
    }

    /// <summary>One list response: the page window plus the metadata counts.</summary>
    private sealed record MemoryListResult(
        JsonElement[] Items,
        int Total,
        int Matching);

    private async Task<MemoryListResult> ListAsync(string query)
    {
        var response = await EnsureClient().GetAsync($"/api/project/memories{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal(JsonValueKind.Object, body.ValueKind);
        var items = body.GetProperty("items").EnumerateArray().ToArray();
        var metadata = body.GetProperty("metadata");
        var total = metadata.GetProperty("total").GetInt32();
        var matching = metadata.GetProperty("matching").GetInt32();
        return new MemoryListResult(items, total, matching);
    }

    private static string ContentOf(JsonElement memory) =>
        memory.GetProperty("content").GetString()!;

    private static string ScopeOf(JsonElement memory) =>
        memory.GetProperty("scope").GetString()!;

    private static string IdOf(JsonElement memory) =>
        memory.GetProperty("id").GetString()!;

    private static string RootOf(JsonElement memory) =>
        memory.GetProperty("project").GetProperty("root").GetString()!;

    private static void TryDeleteDirectory(string path, int retries = 5, int delayMs = 200)
    {
        if (!Directory.Exists(path)) return;
        for (int i = 0; i < retries; i++)
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

    [Fact]
    public async Task GetProjectMemories_WithoutIncludeAncestors_ReturnsOnlyOwnScope()
    {
        await CreateMemoryAsync(_childARoot, "child-a own");
        await CreateMemoryAsync(_parentRoot, "parent own");

        var list = await ListAsync($"?projectRoot={Uri.EscapeDataString(_childARoot)}");

        Assert.Single(list.Items);
        Assert.Equal(1, list.Total);
        Assert.Equal("child-a own", ContentOf(list.Items[0]));
        Assert.Equal("project", ScopeOf(list.Items[0]));
        Assert.Equal(_childARoot.TrimEnd(Path.DirectorySeparatorChar), RootOf(list.Items[0]));
    }

    [Fact]
    public async Task GetProjectMemories_IncludeAncestors_MergesChainAndTagsEachRoot()
    {
        await CreateMemoryAsync(_childARoot, "child-a own");
        await CreateMemoryAsync(_parentRoot, "parent own");

        var list = await ListAsync(
            $"?projectRoot={Uri.EscapeDataString(_childARoot)}&includeAncestors=true");

        Assert.Equal(2, list.Items.Length);
        Assert.Equal(2, list.Total);
        var child = Assert.Single(list.Items, m => ContentOf(m) == "child-a own");
        var parent = Assert.Single(list.Items, m => ContentOf(m) == "parent own");
        // Each memory keeps the level it truly lives in, so delete/edit/promote
        // still target the right scope.
        Assert.Equal(_childARoot.TrimEnd(Path.DirectorySeparatorChar), RootOf(child));
        Assert.Equal(_parentRoot.TrimEnd(Path.DirectorySeparatorChar), RootOf(parent));
        Assert.All(list.Items, m => Assert.Equal("project", ScopeOf(m)));
    }

    [Fact]
    public async Task GetProjectMemories_MultipleRoots_MergesAndDeduplicatesSharedAncestor()
    {
        await CreateMemoryAsync(_parentRoot, "parent shared");
        await CreateMemoryAsync(_childARoot, "child-a own");
        await CreateMemoryAsync(_childBRoot, "child-b own");

        var url =
            $"?project={Uri.EscapeDataString(_childARoot)}&project={Uri.EscapeDataString(_childBRoot)}&includeAncestors=true";
        var list = await ListAsync(url);

        // Three distinct memories, and the shared ancestor appears exactly once
        // even though it is in both chains.
        Assert.Equal(3, list.Items.Length);
        Assert.Equal(3, list.Total);
        Assert.Single(list.Items, m => ContentOf(m) == "parent shared");
        Assert.Single(list.Items, m => ContentOf(m) == "child-a own");
        Assert.Single(list.Items, m => ContentOf(m) == "child-b own");
    }

    [Fact]
    public async Task GetProjectMemories_TierAll_ReturnsSharedAndLocalInOneCall()
    {
        await CreateMemoryAsync(_childARoot, "child-a shared");
        await CreateMemoryAsync(_childARoot, "child-a local", tier: "project-local");

        var list = await ListAsync(
            $"?projectRoot={Uri.EscapeDataString(_childARoot)}&includeAncestors=true&tier=all");

        Assert.Equal(2, list.Items.Length);
        Assert.Equal(2, list.Total);
        var shared = Assert.Single(list.Items, m => ContentOf(m) == "child-a shared");
        var local = Assert.Single(list.Items, m => ContentOf(m) == "child-a local");
        Assert.Equal("project", ScopeOf(shared));
        Assert.Equal("project-local", ScopeOf(local));
        Assert.Equal(_childARoot.TrimEnd(Path.DirectorySeparatorChar), RootOf(local));
    }

    [Fact]
    public async Task GetProjectMemories_DuplicateIdAcrossLevels_NearestLevelWins()
    {
        // The same memory id existing in the workspace and in its ancestor is
        // the real-world duplicate case (a save that matched across levels
        // keeps one ULID). Copy the file rather than saving twice: only a file
        // copy can give both levels the identical id.
        var id = await CreateMemoryAsync(_childARoot, "duplicated across levels");
        var source = Path.Combine(_childARoot, ".eling", "memories", $"{id}.md");
        var ancestorDir = Path.Combine(_parentRoot, ".eling", "memories");
        Directory.CreateDirectory(ancestorDir);
        File.Copy(source, Path.Combine(ancestorDir, $"{id}.md"));

        var list = await ListAsync(
            $"?projectRoot={Uri.EscapeDataString(_childARoot)}&includeAncestors=true");

        // Exactly one copy survives, tagged with the nearest level's root.
        var duplicate = Assert.Single(
            list.Items, m => ContentOf(m) == "duplicated across levels");
        Assert.Equal(1, list.Total);
        Assert.Equal(_childARoot.TrimEnd(Path.DirectorySeparatorChar), RootOf(duplicate));
    }

    [Fact]
    public async Task GetProjectMemories_LocalTierWithoutLocalShard_Returns200Empty()
    {
        await CreateMemoryAsync(_childARoot, "shared only");

        // The legacy local-tier contract must survive the merged read path: a
        // real project that simply has no local shard is an empty view, never
        // a 404.
        var url =
            $"/api/project/memories?projectRoot={Uri.EscapeDataString(_childARoot)}&tier=project-local";
        var response = await EnsureClient().GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Empty(body.GetProperty("items").EnumerateArray().ToArray());
        Assert.Equal(0, body.GetProperty("metadata").GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task GetProjectMemories_GlobalFlag_AppendsGlobalScopeAdditively()
    {
        await CreateMemoryAsync(_childARoot, "child-a own");
        await CreateGlobalMemoryAsync("global own");

        var list = await ListAsync(
            $"?projectRoot={Uri.EscapeDataString(_childARoot)}&includeAncestors=true&global=true");

        Assert.Equal(2, list.Items.Length);
        Assert.Equal(2, list.Total);
        var global = Assert.Single(list.Items, m => ContentOf(m) == "global own");
        Assert.Equal("global", ScopeOf(global));
        // A global memory belongs to no project root; the DTO omits the
        // property entirely rather than serializing a null.
        Assert.False(global.TryGetProperty("project", out _));
        var child = Assert.Single(list.Items, m => ContentOf(m) == "child-a own");
        Assert.Equal("project", ScopeOf(child));
        Assert.Equal(_childARoot.TrimEnd(Path.DirectorySeparatorChar), RootOf(child));
    }

    [Fact]
    public async Task GetProjectMemories_GlobalFlag_ChainWinsTieOverGlobal()
    {
        // The same memory id present in global and in a chain level: the chain
        // level is read first, so its copy wins the dedupe. The global file is
        // copied into the workspace (not the reverse) because a memory's id
        // lives in its content, not in the file name.
        var globalId = await CreateGlobalMemoryAsync("global copy");
        // TestAppBuilder isolates the global store at <tempDir>/user_scope
        // while ELING_DATA_DIR points at <tempDir>/data — derive the store from
        // the env var's parent instead of duplicating the layout here.
        var globalStore = Path.GetFullPath(Path.Combine(
            Environment.GetEnvironmentVariable("ELING_DATA_DIR")!, "..", "user_scope"));
        var source = Path.Combine(globalStore, "memories", $"{globalId}.md");
        Assert.True(File.Exists(source), $"global memory file not found at {source}");
        var childMemoriesDir = Path.Combine(_childARoot, ".eling", "memories");
        Directory.CreateDirectory(childMemoriesDir);
        File.Copy(source, Path.Combine(childMemoriesDir, $"{globalId}.md"));

        var list = await ListAsync(
            $"?projectRoot={Uri.EscapeDataString(_childARoot)}&includeAncestors=true&global=true");

        var tie = Assert.Single(list.Items, m => IdOf(m) == globalId);
        Assert.Equal("project", ScopeOf(tie));
        Assert.Equal(_childARoot.TrimEnd(Path.DirectorySeparatorChar), RootOf(tie));
    }

    [Fact]
    public async Task GetProjectMemories_MetadataTotal_CountsEverythingBeforeThePageWindow()
    {
        for (int i = 0; i < 3; i++)
        {
            await CreateMemoryAsync(_childARoot, $"memory {i}");
        }

        var list = await ListAsync(
            $"?projectRoot={Uri.EscapeDataString(_childARoot)}&includeAncestors=true&limit=1");

        // The page window and the metadata counts answer different questions:
        // one item arrives, but the counts say how many exist and how many
        // match — here identical because no filter is applied.
        Assert.Single(list.Items);
        Assert.Equal(3, list.Total);
        Assert.Equal(3, list.Matching);
    }

    [Fact]
    public async Task GetProjectMemories_TypeFilter_ReturnsOnlyThatTypeWithBothCounts()
    {
        await CreateMemoryAsync(_childARoot, "a fact", type: "Fact");
        await CreateMemoryAsync(_childARoot, "a preference", type: "Preference");

        var list = await ListAsync(
            $"?projectRoot={Uri.EscapeDataString(_childARoot)}&includeAncestors=true&type=Fact");

        // The list is filtered server-side; total is the scope size, matching
        // is what survives the filter — the two numbers the tiles show.
        Assert.Single(list.Items);
        Assert.Equal("Fact", list.Items[0].GetProperty("type").GetString());
        Assert.Equal(2, list.Total);
        Assert.Equal(1, list.Matching);
    }

    [Fact]
    public async Task GetProjectMemories_QueryFilter_MatchesContentOrId()
    {
        await CreateMemoryAsync(_childARoot, "alpha content");
        var betaId = await CreateMemoryAsync(_childARoot, "beta content");

        var byContent = await ListAsync(
            $"?projectRoot={Uri.EscapeDataString(_childARoot)}&includeAncestors=true&q=alpha");
        Assert.Single(byContent.Items);
        Assert.Equal(2, byContent.Total);
        Assert.Equal(1, byContent.Matching);

        // The same query shape that used to run in the browser: substring on
        // content or id, case-insensitive — here proven against the id too.
        var byId = await ListAsync(
            $"?projectRoot={Uri.EscapeDataString(_childARoot)}&includeAncestors=true&q={Uri.EscapeDataString(betaId)}");
        Assert.Single(byId.Items);
        Assert.Equal("beta content", ContentOf(byId.Items[0]));
        Assert.Equal(1, byId.Matching);
    }

    [Fact]
    public async Task GetAggregatedMemories_TypeFilter_FiltersAcrossScopes()
    {
        await CreateGlobalMemoryAsync("global fact", type: "Fact");
        await CreateGlobalMemoryAsync("global preference", type: "Preference");

        var response = await EnsureClient().GetAsync("/api/aggregated/memories?type=Fact");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var items = body.GetProperty("items").EnumerateArray().ToArray();
        Assert.Single(items);
        Assert.Equal("Fact", items[0].GetProperty("type").GetString());
        var metadata = body.GetProperty("metadata");
        Assert.Equal(2, metadata.GetProperty("total").GetInt32());
        Assert.Equal(1, metadata.GetProperty("matching").GetInt32());
    }

    [Fact]
    public async Task GetProjectMemories_UninitializedRoot_EmptyWithAncestors_Legacy404Without()
    {
        var nowhere = Path.Combine(_tempDir, "nowhere");

        var withAncestors = await EnsureClient().GetAsync(
            $"/api/project/memories?projectRoot={Uri.EscapeDataString(nowhere)}&includeAncestors=true");
        Assert.Equal(HttpStatusCode.OK, withAncestors.StatusCode);
        var empty = await withAncestors.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Empty(empty.GetProperty("items").EnumerateArray().ToArray());
        Assert.Equal(0, empty.GetProperty("metadata").GetProperty("total").GetInt32());

        // Without the parameter the legacy answer stands: an unknown root is a
        // 404, exactly as before the chain parameter existed.
        var legacy = await EnsureClient().GetAsync(
            $"/api/project/memories?projectRoot={Uri.EscapeDataString(nowhere)}");
        Assert.Equal(HttpStatusCode.NotFound, legacy.StatusCode);
    }
}
