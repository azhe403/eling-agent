using Eling.Core.Audit;
using Xunit;

namespace Eling.Core.Tests;

public class AuditActorContextTests
{
    [Fact]
    public void Current_WhenUnset_IsUnknown()
    {
        Assert.Equal("unknown", AuditActorContext.Current.Actor);
    }

    [Fact]
    public async Task Begin_ScopesActorToTheAsyncFlow()
    {
        AuditActor? seenInside = null;

        using (AuditActorContext.Begin(new AuditActor("mcp:eling_dev", "mcp_stdio")))
        {
            await Task.Yield();
            seenInside = AuditActorContext.Current;
        }

        Assert.Equal("mcp:eling_dev", seenInside!.Actor);
        Assert.Equal("unknown", AuditActorContext.Current.Actor);
    }

    [Fact]
    public async Task Begin_DoesNotLeakAcrossSiblingTasks()
    {
        var other = await Task.Run(() => AuditActorContext.Current.Actor);

        using (AuditActorContext.Begin(new AuditActor("dashboard", "dashboard_web")))
        {
            Assert.Equal("dashboard", AuditActorContext.Current.Actor);
        }

        Assert.Equal("unknown", other);
    }
}
