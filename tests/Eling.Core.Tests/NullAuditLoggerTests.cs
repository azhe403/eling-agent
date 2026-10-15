using Eling.Core.Audit;
using Xunit;

namespace Eling.Core.Tests;

public class NullAuditLoggerTests
{
    [Fact]
    public async Task RecordAsync_CompletesWithoutThrowing()
    {
        IAuditLogger logger = NullAuditLogger.Instance;

        await logger.RecordAsync(new AuditEvent { Action = "memory_read" });

        Assert.Same(NullAuditLogger.Instance, logger);
    }
}
