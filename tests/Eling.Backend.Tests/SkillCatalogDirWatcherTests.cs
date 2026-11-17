using Eling.Backend;
using Eling.Core.Skill;
using Eling.Core.Skill.Scanning;
using Eling.Core.Skill.Storage;
using Xunit;

namespace Eling.Backend.Tests;

/// <summary>
/// The disk half of the skills live-refresh: a file written to the catalog
/// folder by any process — not via an eling API — is picked up by the dirty
/// drain, rescanned into the SQLite index, and pushed to SSE subscribers as
/// the <c>skills</c> topic. Clear-before-work means a write landing mid-scan
/// re-sets the flag and the next tick rescans.
/// </summary>
public sealed class SkillCatalogDirWatcherTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "eling-skill-dir-watch-" + Guid.NewGuid().ToString("N"));

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), "eling-skill-dir-watch-" + Guid.NewGuid().ToString("N") + ".db");

    private string SkillsDir => Path.Combine(_root, "skills");
    private string SkillFolder(string name) => Path.Combine(SkillsDir, name);
    private string SkillFile(string name) => Path.Combine(SkillFolder(name), "SKILL.md");
    private SqliteSkillIndex NewIndex() => new(_dbPath);

    private MemoryChangeBroadcaster _broadcaster = null!;
    private SqliteSkillIndex _index = null!;
    private SkillCatalogDirWatcher? _watcher;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(SkillsDir);
        _broadcaster = new MemoryChangeBroadcaster();
        _index = NewIndex();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _watcher?.Dispose();

        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException ex)
        {
            // A locked temp catalog must not fail the run.
            System.Diagnostics.Debug.WriteLine($"skill-dir-watch test cleanup: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            System.Diagnostics.Debug.WriteLine($"skill-dir-watch test cleanup: {ex.Message}");
        }

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                File.Delete(_dbPath + suffix);
            }
            catch (IOException ex)
            {
                System.Diagnostics.Debug.WriteLine($"skill-dir-watch test db cleanup: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                System.Diagnostics.Debug.WriteLine($"skill-dir-watch test db cleanup: {ex.Message}");
            }
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task NewSkillFolder_IsIndexedAndBroadcastsSkillsTopic()
    {
        var scanner = new SkillCatalogScanner(_index, _root);
        _watcher = new SkillCatalogDirWatcher(
            scanner, _broadcaster, skillsDirectory: SkillsDir, interval: TimeSpan.FromMilliseconds(50));
        _watcher.Start();

        var received = ReceiveTopicAsync(TimeSpan.FromSeconds(10));

        // A neutral external-style skill folder appears on disk, unseen by any eling API.
        Directory.CreateDirectory(SkillFolder("external-helper"));
        await File.WriteAllTextAsync(
            SkillFile("external-helper"),
            "---\nname: external-helper\ndescription: supplied by another tool\n---\n# external-helper\n");

        Assert.Equal(SkillChangeTopics.Skills, await received);
        // The rescan ran before the ping: the row is now in the index.
        var row = await _index.GetByNameAsync("external-helper");
        Assert.NotNull(row);
    }

    [Fact]
    public async Task ChangeDuringScan_TriggersAnotherBroadcast()
    {
        // A scanner stub that drops a new skill file into the watched dir on
        // its first ScanAsync() call must cause the watcher to see the write
        // and scan again — clear-before-work.
        var inner = new SkillCatalogScanner(_index, _root);
        _watcher = new SkillCatalogDirWatcher(
            new ScanThenWriteScanner(inner, SkillsDir),
            _broadcaster,
            skillsDirectory: SkillsDir,
            interval: TimeSpan.FromMilliseconds(50));
        _watcher.Start();

        var received = ReceiveTopicsAsync(expectedCount: 2, TimeSpan.FromSeconds(10));

        // Trigger one write to start things.
        Directory.CreateDirectory(SkillFolder("trigger-helper"));
        await File.WriteAllTextAsync(
            SkillFile("trigger-helper"),
            "---\nname: trigger-helper\ndescription: supplied by another tool\n---\n# trigger-helper\n");

        var frames = await received;
        Assert.True(frames.Count >= 2,
            $"Expected at least 2 skills frames (one from the trigger, one from the mid-scan write), got {frames.Count}.");
        Assert.All(frames, frame => Assert.Equal(SkillChangeTopics.Skills, frame));
    }

    private async Task<string> ReceiveTopicAsync(TimeSpan timeout)
    {
        var frames = await ReceiveTopicsAsync(expectedCount: 1, timeout);
        if (frames.Count == 0)
        {
            throw new TimeoutException("No skills broadcast arrived within the timeout.");
        }

        return frames[0];
    }

    private async Task<List<string>> ReceiveTopicsAsync(int expectedCount, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var frames = new List<string>();
        try
        {
            await foreach (var item in _broadcaster.SubscribeAsync(cts.Token))
            {
                frames.Add(item);
                if (frames.Count >= expectedCount) break;
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout: return whatever arrived so the caller can assert on it.
            System.Diagnostics.Debug.WriteLine("skill-dir-watch test receive: timed out waiting for frames.");
        }

        return frames;
    }

    private sealed class ScanThenWriteScanner : ISkillCatalogScanner
    {
        private readonly ISkillCatalogScanner _inner;
        private readonly string _skillsDir;
        private int _calls;

        public ScanThenWriteScanner(ISkillCatalogScanner inner, string skillsDir)
        {
            _inner = inner;
            _skillsDir = skillsDir;
        }

        public async Task<ScanReport> ScanAsync()
        {
            if (Interlocked.Exchange(ref _calls, 1) == 0)
            {
                // External-style write from "another tool" landing mid-scan.
                var folder = Path.Combine(_skillsDir, "mid-scan-helper");
                Directory.CreateDirectory(folder);
                await File.WriteAllTextAsync(
                    Path.Combine(folder, "SKILL.md"),
                    "---\nname: mid-scan-helper\ndescription: supplied by another tool\n---\n# mid-scan-helper\n");
            }

            return await _inner.ScanAsync();
        }
    }
}
