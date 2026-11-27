using System.Text.Json;
using Eling.Backend;
using Eling.Core.Docs;
using Eling.Core.Docs.Storage;
using Xunit;

namespace Eling.Backend.Tests;

public class DocsDirWatcherTests : IDisposable
{
    private readonly string _rootA;
    private readonly string _rootB;
    private readonly string _dbPath;
    private readonly DocsService _service;
    private readonly MemoryChangeBroadcaster _broadcaster = new();

    public DocsDirWatcherTests()
    {
        var key = Guid.NewGuid().ToString("N");
        _rootA = Path.Combine(Path.GetTempPath(), $"eling-watch-a-{key}");
        _rootB = Path.Combine(Path.GetTempPath(), $"eling-watch-b-{key}");
        Directory.CreateDirectory(Path.Combine(_rootA, "docs"));
        Directory.CreateDirectory(Path.Combine(_rootB, "docs"));
        _dbPath = Path.Combine(Path.GetTempPath(), $"eling-watch-{key}.db");
        _service = new DocsService(
            new SqliteDocsIndex(_dbPath),
            new SqliteDocsAuditStore(_dbPath + ".audit"),
            [_rootA]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_rootA, true); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
        try { Directory.Delete(_rootB, true); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
        try { File.Delete(_dbPath); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
        try { File.Delete(_dbPath + ".audit"); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
    }

    private static void Write(string root, string file, string content)
    {
        var path = Path.Combine(root, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public async Task ExternalMarkdownEdit_ReindexesThatWorkspaceOnly()
    {
        Write(_rootA, "docs/specs/a.md", "# Alpha\n\nBody");
        using var watcher = new DocsDirWatcher(_service, _broadcaster);
        watcher.Start();
        Assert.True(await WaitFor(() => _service.ListAsync().ContinueWith(t => t.Result.Count == 1)));

        Write(_rootA, "docs/specs/a.md", "# Alpha Updated\n\nBody");
        var notified = await WaitForPayload();
        Assert.Equal(DocsChangeTopics.Docs, notified?.Topic);
        var item = Assert.Single(await _service.ListAsync());
        Assert.Equal("Alpha Updated", item.Title);
    }

    [Fact]
    public async Task StartScansEveryActiveWorkspaceToCatchEditsWhileDashboardWasDown()
    {
        Write(_rootA, "docs/specs/a.md", "# Alpha\n\nBody");
        using var watcher = new DocsDirWatcher(_service, _broadcaster);
        watcher.Start();
        Assert.True(await WaitFor(() => _service.ListAsync().ContinueWith(t => t.Result.Count == 1)));
    }

    [Fact]
    public async Task WorkspaceRegistryChangeRearmsWatcherSet()
    {
        Write(_rootB, "docs/specs/b.md", "# Beta\n\nBody");
        using var watcher = new DocsDirWatcher(_service, _broadcaster);
        watcher.Start();
        _service.SetWorkspaceRoots([_rootA, _rootB]);
        Write(_rootB, "docs/specs/b.md", "# Beta Updated\n\nBody");
        var notified = await WaitForPayload();
        Assert.Equal(DocsChangeTopics.Docs, notified?.Topic);
        Assert.True(await WaitFor(() => _service.ListAsync().ContinueWith(t => t.Result.Any(item => item.Title == "Beta Updated"))));
    }

    [Fact]
    public async Task UncoveredMarkdownChange_DoesNotBroadcast()
    {
        using var watcher = new DocsDirWatcher(_service, _broadcaster);
        watcher.Start();
        // A newly armed workspace always scans and broadcasts once at startup.
        Assert.NotNull(await WaitForPayload());

        Write(_rootA, "node_modules/pkg/readme.md", "# Ignored\n\nBody");

        Assert.Null(await WaitForPayload(timeoutMs: 2200));
        Assert.Empty(await _service.ListAsync());
    }

    [Fact]
    public async Task DeletedMarkdown_RemovesItsIndexedRow()
    {
        Write(_rootA, "docs/specs/gone.md", "# Gone\n\nBody");
        using var watcher = new DocsDirWatcher(_service, _broadcaster);
        watcher.Start();
        Assert.True(await WaitFor(() => _service.ListAsync().ContinueWith(t => t.Result.Count == 1)));

        File.Delete(Path.Combine(_rootA, "docs/specs/gone.md"));

        Assert.True(await WaitFor(() => _service.ListAsync().ContinueWith(t => t.Result.Count == 0)));
    }

    [Fact]
    public async Task Broadcast_CarriesWorkspaceIdAndRootOfChangedWorkspace()
    {
        Write(_rootA, "docs/specs/a.md", "# Alpha\n\nBody");
        using var watcher = new DocsDirWatcher(_service, _broadcaster);
        watcher.Start();
        Assert.True(await WaitFor(() => _service.ListAsync().ContinueWith(t => t.Result.Count == 1)));

        Write(_rootA, "docs/specs/a.md", "# Alpha Updated\n\nBody");

        var payload = await WaitForPayload();
        Assert.Equal(DocsChangeTopics.Docs, payload?.Topic);
        Assert.Equal(DocsService.ResolveWorkspaceId(_rootA), payload?.WorkspaceId);
        Assert.Equal(_rootA, payload?.Workspace);
    }

    private static async Task<bool> WaitFor(Func<Task<bool>> predicate, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await predicate()) return true;
            await Task.Delay(100);
        }
        return false;
    }

    private async Task<DocsChangePayload?> WaitForPayload(int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await foreach (var reason in _broadcaster.SubscribeAsync(cts.Token))
            {
                if (string.IsNullOrWhiteSpace(reason) || reason == "connected") continue;
                if (!reason.StartsWith('{'))
                    return new DocsChangePayload(reason, null, null);
                using var json = JsonDocument.Parse(reason);
                var root = json.RootElement;
                return new DocsChangePayload(
                    root.GetProperty("topic").GetString() ?? "",
                    root.GetProperty("workspace_id").GetString(),
                    root.GetProperty("workspace").GetString());
            }
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine($"No docs event within {timeoutMs} ms; treating as no event.");
        }
        return null;
    }

    [Fact]
    public async Task ScanGateBusy_ReArmsDirtyFlagSoTheChangedFileIsIndexedLater()
    {
        var index = new GateBlockingIndex();
        var service = new DocsService(index, new SqliteDocsAuditStore(_dbPath + ".busy"), [_rootA]);
        Write(_rootA, "docs/manual.md", "# Manual\n\nBody");
        var blockedScan = Task.Run(() => service.ScanAsync(
            paths: [Path.Combine(_rootA, "docs/manual.md")], workspaceRoot: _rootA));
        await index.WaitForBlockedAsync();

        using var watcher = new DocsDirWatcher(service, _broadcaster);
        watcher.Start();
        Write(_rootA, "docs/event.md", "# Event\n\nBody");

        // Two watcher ticks land while the gate is held; both must re-arm.
        await Task.Delay(2300);
        index.Release();
        await blockedScan;

        Assert.True(await WaitFor(() => Task.FromResult(index.WasIndexed("docs/event.md"))));
    }

    private sealed class GateBlockingIndex : IDocsIndex
    {
        private readonly object _sync = new();
        private readonly HashSet<string> _indexed = new(StringComparer.OrdinalIgnoreCase);
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitForBlockedAsync() => _entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        public void Release() => _release.TrySetResult();
        public bool WasIndexed(string path) { lock (_sync) return _indexed.Contains(path); }

        public async Task<Doc?> GetByPathAsync(string workspaceId, string path, CancellationToken ct = default)
        {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(ct);
            return null;
        }

        public Task IndexAsync(Doc doc, IReadOnlyList<DocSection> sections, IReadOnlyList<DocRef> refs, string? body = null)
        {
            lock (_sync) _indexed.Add(doc.Path.Replace('\\', '/'));
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string docId) => Task.CompletedTask;
        public Task RebuildAsync(IEnumerable<DocRebuildItem> items) => Task.CompletedTask;
        public Task<Doc?> GetAsync(string docId) => Task.FromResult<Doc?>(null);
        public Task<DocSection?> GetSectionAsync(string docId, string anchor) => Task.FromResult<DocSection?>(null);
        public Task<IReadOnlyList<DocSection>> GetSectionsAsync(string docId)
            => Task.FromResult<IReadOnlyList<DocSection>>(Array.Empty<DocSection>());
        public Task<IReadOnlyList<DocRef>> GetRefsAsync(string docId)
            => Task.FromResult<IReadOnlyList<DocRef>>(Array.Empty<DocRef>());
        public Task<IReadOnlyList<DocRelation>> GetRelationsAsync(string docId, bool asSource)
            => Task.FromResult<IReadOnlyList<DocRelation>>(Array.Empty<DocRelation>());
        public Task UpsertRelationAsync(DocRelation relation) => Task.CompletedTask;
        public Task DeleteRelationAsync(string sourceId, string targetId, RelationKind kind) => Task.CompletedTask;
        public Task<CoveragePolicy> GetPolicyAsync(CancellationToken ct = default)
            => Task.FromResult(new CoveragePolicy());
        public Task SetPolicyAsync(CoveragePolicy policy, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<DocSearchResult>> SearchAsync(
            string query, string? workspaceId = null, DocKind? kind = null, string? topic = null,
            string? pathPrefix = null, int limit = 10)
            => Task.FromResult<IReadOnlyList<DocSearchResult>>(Array.Empty<DocSearchResult>());
        public Task<IReadOnlyList<DocListItem>> ListAsync(
            string? workspaceId = null, DocKind? kind = null, string? topic = null,
            string sort = "name", string order = "asc", bool includeArchived = false)
            => Task.FromResult<IReadOnlyList<DocListItem>>(Array.Empty<DocListItem>());
    }

    private sealed record DocsChangePayload(
        string Topic,
        string? WorkspaceId,
        string? Workspace);
}
