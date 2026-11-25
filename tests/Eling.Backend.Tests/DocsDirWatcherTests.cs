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
        var notified = await WaitForReason(DocsChangeTopics.Docs);
        Assert.Equal(DocsChangeTopics.Docs, notified);
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
        var notified = await WaitForReason(DocsChangeTopics.Docs);
        Assert.Equal(DocsChangeTopics.Docs, notified);
        Assert.True(await WaitFor(() => _service.ListAsync().ContinueWith(t => t.Result.Any(item => item.Title == "Beta Updated"))));
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

    private async Task<string?> WaitForReason(string root, int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        await foreach (var reason in _broadcaster.SubscribeAsync(cts.Token))
        {
            if (string.Equals(reason, root, StringComparison.OrdinalIgnoreCase)) return reason;
        }
        return null;
    }
}
