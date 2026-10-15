using System.Text.Json;
using Eling.Core.Audit;
using Eling.Core.Memory;
using Xunit;

namespace Eling.Core.Tests;

public class AuditEventSerializationTests
{
    [Fact]
    public void Serialize_ChangeEntry_CarriesStateSnapshotsAndOmitsNulls()
    {
        var entry = new AuditEvent
        {
            Timestamp = new DateTimeOffset(2026, 9, 11, 9, 30, 0, TimeSpan.Zero),
            Trail = AuditTrail.Change,
            Category = AuditCategory.Memory,
            Action = "memory_update",
            Actor = "mcp:eling_dev",
            Source = "mcp_stdio",
            Scope = MemoryScopeKind.Project,
            Target = "01m0000000000000000000000",
            Metadata = new AuditMetadata { Content = "hello", Tags = ["audit"], Status = "active" },
            PreviousMetadata = new AuditMetadata { Content = "old" }
        };

        var json = JsonSerializer.Serialize(entry, AuditJsonContext.Default.AuditEvent);

        Assert.DoesNotContain("\n", json);
        Assert.Contains("\"trail\":\"change\"", json);
        Assert.Contains("\"category\":\"memory\"", json);
        Assert.Contains("\"scope\":\"project\"", json);
        Assert.Contains("\"previousMetadata\":", json);
        Assert.DoesNotContain("\"hash\"", json); // null fields are omitted on disk
    }

    [Fact]
    public void Serialize_AccessEntry_KeepsDetailFlatAndOmitsState()
    {
        var entry = new AuditEvent
        {
            Timestamp = DateTimeOffset.UnixEpoch,
            Trail = AuditTrail.Access,
            Category = AuditCategory.Filesystem,
            Action = "fs_read",
            Actor = "dashboard",
            Source = "dashboard_web",
            Target = "src/a.cs",
            Path = "src/a.cs",
            ByteSize = 128
        };

        var json = JsonSerializer.Serialize(entry, AuditJsonContext.Default.AuditEvent);
        var back = JsonSerializer.Deserialize(json, AuditJsonContext.Default.AuditEvent)!;

        Assert.Equal(1, back.SchemaVersion);
        Assert.Equal(AuditTrail.Access, back.Trail);
        Assert.Equal("fs_read", back.Action);
        Assert.Equal("src/a.cs", back.Path);
        Assert.Equal(128, back.ByteSize);
        Assert.Null(back.Metadata);
    }
}
