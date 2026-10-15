using System.Text.Json;
using Eling.Core.Audit;
using Eling.Core.Audit.Storage;
using Xunit;

namespace Eling.Core.Tests;

public class BufferedAuditLoggerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "eling-audit-buf-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RecordAsync_ChangeTrail_IsDurableBeforeReturning()
    {
        var store = new AuditFileStore(_root, TimeProvider.System);
        await using var logger = new BufferedAuditLogger(store, index: null, new AuditOptions(), TimeProvider.System);

        using (AuditActorContext.Begin(new AuditActor("mcp:eling_dev", "mcp_stdio")))
        {
            await logger.RecordAsync(new AuditEvent
            {
                Trail = AuditTrail.Change,
                Category = AuditCategory.Memory,
                Action = "memory_save",
                Timestamp = DateTimeOffset.UtcNow
            });
        }

        var path = store.GetCurrentFilePath(AuditTrail.Change, DateTimeOffset.UtcNow);
        var line = (await File.ReadAllLinesAsync(path)).Single();
        var entry = JsonSerializer.Deserialize(line, AuditJsonContext.Default.AuditEvent)!;
        Assert.Equal("mcp:eling_dev", entry.Actor);
        Assert.Equal("mcp_stdio", entry.Source);
    }

    [Fact]
    public async Task DisposeAsync_DrainsPendingAccessEntries()
    {
        var store = new AuditFileStore(_root, TimeProvider.System);
        var logger = new BufferedAuditLogger(store, index: null, new AuditOptions(), TimeProvider.System);

        await logger.RecordAsync(new AuditEvent
        {
            Trail = AuditTrail.Access,
            Category = AuditCategory.Memory,
            Action = "memory_read",
            Timestamp = DateTimeOffset.UtcNow
        });

        await logger.DisposeAsync();

        var path = store.GetCurrentFilePath(AuditTrail.Access, DateTimeOffset.UtcNow);
        Assert.True(File.Exists(path));
        Assert.Single(await File.ReadAllLinesAsync(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
