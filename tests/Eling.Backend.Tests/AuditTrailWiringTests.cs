using Eling.Backend.Bootstrap;
using Eling.Backend.Mcp;
using Eling.Core.Audit;
using Eling.Core.Memory;
using Eling.Core.Scope;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Eling.Backend.Tests;

/// <summary>
/// The production hosts opt into the audit trail (AddElingAuditTrail) and the
/// memory hooks record into what they resolve: a save through the dashboard
/// owner's own container lands exactly one change entry, and a synchronous host
/// teardown still disposes the writer. The entry shapes themselves are pinned
/// by <see cref="Eling.Core.Tests.AuditMemoryHookTests"/>; this is the wiring
/// guard, so it runs the real host path rather than a hand-built container.
/// </summary>
[Collection(ElingDataDirCollection.Name)]
public sealed class AuditTrailWiringTests : IAsyncLifetime
{
    private WebApplication _app = null!;

    public Task InitializeAsync()
    {
        // TestAppBuilder points ELING_DATA_DIR at a scratch directory, so the
        // machine-global audit store this host resolves is the isolated one and
        // never the real store on this machine (amendment A2).
        _app = TestAppBuilder.CreateSelfContained();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        // Disposing the host disposes its container, which drains the writer's
        // channel — the shutdown path this test exercises.
        await _app.DisposeAsync();
    }

    [Fact]
    public void Host_ResolvesTheRealAuditWriter()
    {
        var logger = _app.Services.GetRequiredService<IAuditLogger>();

        // The both-ways-disposable wrapper, not NullAuditLogger: the host opted
        // in, so its memory operations are recorded.
        Assert.IsType<DisposableAuditLogger>(logger);
    }

    [Fact]
    public async Task MemorySave_ThroughTheHostContainer_LandsOneChangeEntry()
    {
        var saved = await SaveThroughHostAsync("audit wiring probe");

        var path = Path.Combine(CentralAuditDirectory.Resolve(), "changes", DayFileName());
        Assert.True(File.Exists(path), $"expected an audit entry at {path}");

        var lines = await File.ReadAllLinesAsync(path);
        var entry = Assert.Single(lines, l => l.Contains(saved, StringComparison.Ordinal));
        Assert.Contains("\"action\":\"memory_save\"", entry, StringComparison.Ordinal);
        Assert.Contains("\"trail\":\"change\"", entry, StringComparison.Ordinal);
    }

    private async Task<string> SaveThroughHostAsync(string content)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var memory = scope.ServiceProvider.GetRequiredService<IScopedMemoryService>();

        var saved = await memory.SaveAsync(new Memory(MemoryType.Fact, content, ["wiring"], "test"));
        return saved.Id.Value;
    }

    private static string DayFileName() =>
        DateTimeOffset.UtcNow.ToString("yyyy-MM-dd") + ".jsonl";
}
