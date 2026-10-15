using Eling.Core.Scope;
using Xunit;

namespace Eling.Core.Tests;

public sealed class CentralAuditDirectoryTests
{
    [Fact]
    public void Resolve_IsSiblingOfLogsUnderElingRoot()
    {
        var home = Path.Combine(Path.GetTempPath(), $"eling-audit-dir-{Guid.NewGuid():N}");
        var expected = Path.Combine(home, ".local", "share", "eling", "audit");

        var actual = CentralAuditDirectory.Resolve(userHome: home);

        Assert.Equal(expected, actual);
        Assert.True(Directory.Exists(actual));
    }

    [Fact]
    public void Resolve_HonoursXdgDataHome()
    {
        var xdg = Path.Combine(Path.GetTempPath(), $"eling-audit-xdg-{Guid.NewGuid():N}");
        var expected = Path.Combine(xdg, "eling", "audit");

        var actual = CentralAuditDirectory.Resolve(xdgDataHome: xdg, userHome: "/home/user");

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Resolve_HonoursElingDataDirOverride()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"eling-audit-data-{Guid.NewGuid():N}");
        var expected = Path.Combine(dataDir, "audit");

        var actual = CentralAuditDirectory.Resolve(elingDataDir: dataDir);

        Assert.Equal(expected, actual);
        Assert.True(Directory.Exists(actual));
    }
}
