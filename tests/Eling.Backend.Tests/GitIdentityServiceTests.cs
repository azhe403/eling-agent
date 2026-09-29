using System.Collections.Generic;
using Eling.Backend.Identity;

namespace Eling.Backend.Tests;

/// <summary>
/// Canned git config so the fallback paths are exercised without depending on
/// whatever global git config the machine running the tests happens to have.
/// </summary>
public sealed class FakeGitConfigReader(Dictionary<string, string?> values) : IGitConfigReader
{
    public List<string> RequestedKeys { get; } = [];

    public Task<string?> ReadGlobalAsync(string key, CancellationToken cancellationToken)
    {
        RequestedKeys.Add(key);
        return Task.FromResult(values.TryGetValue(key, out var value) ? value : null);
    }
}

public class GitIdentityServiceTests
{
    private static GitIdentityService ServiceOver(Dictionary<string, string?> values)
        => new(new FakeGitConfigReader(values));

    [Fact]
    public async Task GetIdentityAsync_ReturnsConfiguredNameAndEmail()
    {
        var service = ServiceOver(new()
        {
            ["user.name"] = "Azhe Kun",
            ["user.email"] = "root@corp.azhe.my.id",
        });

        var identity = await service.GetIdentityAsync(CancellationToken.None);

        Assert.Equal("Azhe Kun", identity.Name);
        Assert.Equal("root@corp.azhe.my.id", identity.Email);
    }

    [Fact]
    public async Task GetIdentityAsync_TrimsSurroundingWhitespace()
    {
        // git can echo back a padded value depending on how it was set.
        var service = ServiceOver(new()
        {
            ["user.name"] = "  Azhe Kun \n",
            ["user.email"] = "\troot@corp.azhe.my.id  ",
        });

        var identity = await service.GetIdentityAsync(CancellationToken.None);

        Assert.Equal("Azhe Kun", identity.Name);
        Assert.Equal("root@corp.azhe.my.id", identity.Email);
    }

    [Fact]
    public async Task GetIdentityAsync_UnsetKeys_FallsBackToAnonymousAndEmptyEmail()
    {
        var service = ServiceOver(new());

        var identity = await service.GetIdentityAsync(CancellationToken.None);

        Assert.Equal("anonymous", identity.Name);
        Assert.Equal(string.Empty, identity.Email);
    }

    [Fact]
    public async Task GetIdentityAsync_NameSetButEmailUnset_KeepsNameAndBlanksEmail()
    {
        // The two keys are independent: git can have one without the other, and
        // a missing email must not drag the name down to the fallback.
        var service = ServiceOver(new() { ["user.name"] = "Azhe Kun" });

        var identity = await service.GetIdentityAsync(CancellationToken.None);

        Assert.Equal("Azhe Kun", identity.Name);
        Assert.Equal(string.Empty, identity.Email);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetIdentityAsync_BlankName_FallsBackToAnonymous(string configured)
    {
        var service = ServiceOver(new() { ["user.name"] = configured });

        var identity = await service.GetIdentityAsync(CancellationToken.None);

        Assert.Equal("anonymous", identity.Name);
    }

    [Fact]
    public async Task GetIdentityAsync_ReadsBothGlobalKeys()
    {
        var reader = new FakeGitConfigReader(new()
        {
            ["user.name"] = "Azhe Kun",
            ["user.email"] = "root@corp.azhe.my.id",
        });
        var service = new GitIdentityService(reader);

        await service.GetIdentityAsync(CancellationToken.None);

        Assert.Equal(["user.name", "user.email"], reader.RequestedKeys);
    }
}
