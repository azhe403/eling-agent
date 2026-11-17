using Eling.Backend;
using Eling.Backend.Services;
using Eling.Core.Skill;
using Eling.Core.Skill.Storage;
using Xunit;

namespace Eling.Backend.Tests;

/// <summary>
/// The mutation-side relay for the <c>skills</c> SSE topic: the owner's
/// in-proc notifier pushes the topic onto the shared change stream, and
/// SkillService really fires ISkillChangeNotifier on save — the assumption
/// the whole cross-process relay rests on.
/// </summary>
public sealed class SkillChangeNotifierTests : IAsyncLifetime
{
    private readonly string _catalogRoot = Path.Combine(
        Path.GetTempPath(), "eling-skill-notify-" + Guid.NewGuid().ToString("N"));

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), "eling-skill-notify-" + Guid.NewGuid().ToString("N") + ".db");

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        if (Directory.Exists(_catalogRoot)) Directory.Delete(_catalogRoot, recursive: true);
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task BroadcasterSkillChangeNotifier_NotifyAsync_BroadcastsSkillsTopic()
    {
        var broadcaster = new MemoryChangeBroadcaster();
        var notifier = new BroadcasterSkillChangeNotifier(broadcaster);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = ReceiveFirstAsync(broadcaster, cts.Token);
        await notifier.NotifyAsync("saved");

        Assert.Equal(SkillChangeTopics.Skills, await received);
    }

    [Fact]
    public async Task SaveAsync_InvokesTheWiredSkillChangeNotifier()
    {
        // Core graph, temp catalog: SkillService with a recording notifier.
        // Asserts SkillService really fires on save — the assumption the whole
        // relay rests on.
        var recording = new RecordingNotifier();
        var service = new SkillService(
            new FileSystemSkillStorage(_catalogRoot),
            new SqliteSkillIndex(_dbPath),
            recording);

        await service.SaveAsync(new SkillSaveInput("fixture-skill", "desc", "# body"));

        Assert.True(recording.Calls >= 1);
    }

    private static async Task<string> ReceiveFirstAsync(MemoryChangeBroadcaster broadcaster, CancellationToken ct)
    {
        await foreach (var item in broadcaster.SubscribeAsync(ct))
        {
            return item;
        }

        throw new TimeoutException("No skills broadcast arrived within the timeout.");
    }

    private sealed class RecordingNotifier : ISkillChangeNotifier
    {
        public int Calls;
        public Task NotifyAsync(string reason = "mutation", CancellationToken ct = default)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }
}
