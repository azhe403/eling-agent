using Eling.Backend.Shell;
using Eling.Core.Scope;
using Eling.Core.Shell;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Eling.Backend.Tests.Shell;

/// <summary>
/// The shell configuration decides whether a command may run at all and where
/// it may run, so these tests pin the two properties everything else depends
/// on: the file starts out safe (tool disabled, guardrails on), and every
/// mutation round-trips through the file.
/// </summary>
/// <remarks>
/// Several processes (the dashboard host and each MCP stdio host) share one
/// configuration file while holding their own store singleton, so the store
/// must also observe changes written by other processes — see the
/// ...Externally... tests below — and must never hand a consumer a rule that
/// is inert or, worse, matches everything.
/// </remarks>
public sealed class ShellConfigStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"eling-shell-{Guid.NewGuid():N}");

    private string ConfigPath => Path.Combine(_root, "config", "shell-config.json");

    private ShellConfigStore NewStore()
    {
        Directory.CreateDirectory(_root);
        return new ShellConfigStore(new UserScope(_root), NullLogger<ShellConfigStore>.Instance);
    }

    /// <summary>Writes the configuration file directly, simulating another process mutating it.</summary>
    private void WriteConfig(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, json);
    }

    [Fact]
    public void FreshStore_CreatesTemplateWithSafeDefaults()
    {
        var store = NewStore();

        var config = store.GetConfig();

        Assert.True(File.Exists(ConfigPath));
        Assert.False(config.Enabled);
        Assert.True(config.RestrictToAllowedDirectories);
        Assert.True(config.RootGuardrail);
        Assert.Equal(120, config.DefaultTimeoutSeconds);
        Assert.Equal(65536, config.MaxOutputBytes);
        Assert.Empty(config.AllowedDirectories);
        Assert.Empty(config.BlockedCommands);

        var raw = File.ReadAllText(ConfigPath);
        Assert.Contains("\"enabled\"", raw);
        Assert.Contains("\"root_guardrail\"", raw);
        Assert.Contains("\"blocked_commands\"", raw);
    }

    [Fact]
    public void Update_PersistsTheMutationToDisk()
    {
        var store = NewStore();

        var updated = store.Update(config => config with
        {
            Enabled = true,
            BlockedCommands = [.. config.BlockedCommands, new BlockedCommandRule { Pattern = "git checkout" }],
        });

        Assert.True(updated.Enabled);

        var persisted = NewStore().GetConfig();
        Assert.True(persisted.Enabled);
        var rule = Assert.Single(persisted.BlockedCommands);
        Assert.Equal("git checkout", rule.Pattern);
        Assert.True(rule.Enabled);
        Assert.Contains("git checkout", File.ReadAllText(ConfigPath));
    }

    /// <summary>
    /// The store owns the mutation stamp: a caller cannot backdate it, and a
    /// reader in another process needs it to tell a fresh file from a stale
    /// one.
    /// </summary>
    [Fact]
    public void Update_StampsUpdatedAtFromTheStore()
    {
        var store = NewStore();
        var stale = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var before = DateTimeOffset.UtcNow.AddMinutes(-1);

        var updated = store.Update(config => config with { UpdatedAt = stale });

        Assert.NotEqual(stale, updated.UpdatedAt);
        Assert.True(updated.UpdatedAt >= before);
        Assert.True(updated.UpdatedAt <= DateTimeOffset.UtcNow.AddMinutes(1));
    }

    [Fact]
    public void GetConfig_WhenFileChangedExternally_ReflectsNewState()
    {
        var store = NewStore();

        WriteConfig("""
            { "enabled": true, "blocked_commands": [ { "pattern": "git checkout" } ] }
            """);

        var config = store.GetConfig();

        Assert.True(config.Enabled);
        Assert.Equal("git checkout", Assert.Single(config.BlockedCommands).Pattern);
    }

    [Fact]
    public void GetConfig_WhenExternalFileIsCorrupt_KeepsLastGoodSnapshotAndTheCorruptFile()
    {
        var store = NewStore();
        store.Update(config => config with { Enabled = true });

        WriteConfig("{ not json");

        Assert.True(store.GetConfig().Enabled);
        Assert.Equal("{ not json", File.ReadAllText(ConfigPath));
    }

    /// <summary>
    /// A corrupt file is kept for inspection, but it must not be sticky: once
    /// the file is repaired, the next read picks it up.
    /// </summary>
    [Fact]
    public void GetConfig_AfterTheCorruptFileIsRepaired_ReadsTheRepairedFile()
    {
        var store = NewStore();
        store.Update(config => config with { Enabled = true });

        WriteConfig("{ not json");
        _ = store.GetConfig();

        WriteConfig("""
            { "enabled": false }
            """);

        Assert.False(store.GetConfig().Enabled);
    }

    /// <summary>
    /// A mutation must start from what another process wrote, not from the
    /// snapshot this store last happened to hold — otherwise the next save
    /// silently reverts the other process's change.
    /// </summary>
    [Fact]
    public void Update_AfterExternalChange_DoesNotRevertTheExternalState()
    {
        var store = NewStore();
        store.Update(config => config with { Enabled = true });

        WriteConfig("""
            { "enabled": true, "blocked_commands": [ { "pattern": "git push" } ] }
            """);

        store.Update(config => config with { RootGuardrail = false });

        var persisted = NewStore().GetConfig();
        Assert.True(persisted.Enabled);
        Assert.False(persisted.RootGuardrail);
        Assert.Equal("git push", Assert.Single(persisted.BlockedCommands).Pattern);
    }

    /// <summary>
    /// A deleted file must not silently reset the configuration to the
    /// template: for a security switch, "the file is gone" means "keep the last
    /// state I was told about", not "turn it off".
    /// </summary>
    [Fact]
    public void GetConfig_WhenFileDeletedExternally_KeepsLastGoodSnapshot()
    {
        var store = NewStore();
        store.Update(config => config with { Enabled = true });

        File.Delete(ConfigPath);

        Assert.True(store.GetConfig().Enabled);
        Assert.False(File.Exists(ConfigPath));
    }

    /// <summary>
    /// An empty path can never match a directory, and an empty pattern matches
    /// every command (<c>"x".Contains("")</c>), so both are dropped before any
    /// consumer sees them; the values that survive are trimmed.
    /// </summary>
    [Fact]
    public void GetConfig_DropsRulesWhosePathOrPatternIsBlankAndTrimsTheRest()
    {
        var store = NewStore();

        WriteConfig("""
            {
              "allowed_directories": [
                { "path": null },
                { "path": "" },
                { "path": "   " },
                { "path": "  C:\\Projexts\\Shared  " }
              ],
              "blocked_commands": [
                { "pattern": null },
                { "pattern": "" },
                { "pattern": "   " },
                { "pattern": "  git checkout  " }
              ]
            }
            """);

        var config = store.GetConfig();

        var directory = Assert.Single(config.AllowedDirectories);
        Assert.Equal("C:\\Projexts\\Shared", directory.Path);
        var blocked = Assert.Single(config.BlockedCommands);
        Assert.Equal("git checkout", blocked.Pattern);
    }

    /// <summary>
    /// A <c>null</c> list entry survives deserialization into the list, so the
    /// sanitizer has to drop it rather than let a consumer dereference it.
    /// </summary>
    [Fact]
    public void GetConfig_DropsNullRuleEntries()
    {
        var store = NewStore();

        WriteConfig("""
            {
              "allowed_directories": [ null, { "path": "C:\\Shared" } ],
              "blocked_commands": [ null, { "pattern": "git checkout" } ]
            }
            """);

        var config = store.GetConfig();

        Assert.Equal("C:\\Shared", Assert.Single(config.AllowedDirectories).Path);
        Assert.Equal("git checkout", Assert.Single(config.BlockedCommands).Pattern);
    }

    [Fact]
    public void Update_DropsRulesThatWouldBeInert()
    {
        var store = NewStore();

        var config = store.Update(current => current with
        {
            AllowedDirectories = [new AllowedDirectoryRule { Path = "   " }],
            BlockedCommands = [new BlockedCommandRule { Pattern = "" }],
        });

        Assert.Empty(config.AllowedDirectories);
        Assert.Empty(config.BlockedCommands);

        var persisted = NewStore().GetConfig();
        Assert.Empty(persisted.AllowedDirectories);
        Assert.Empty(persisted.BlockedCommands);
    }

    [Fact]
    public void GetConfig_ReturnsACopySoCallersCannotMutateTheSnapshot()
    {
        var store = NewStore();
        store.Update(config => config with
        {
            BlockedCommands = [new BlockedCommandRule { Pattern = "git checkout" }],
        });

        store.GetConfig().BlockedCommands.Clear();

        Assert.Single(store.GetConfig().BlockedCommands);
    }

    [Fact]
    public void ToggleBlockedCommand_FlipsOnlyTheMatchingRuleAndStampsIt()
    {
        var store = NewStore();
        var before = DateTimeOffset.UtcNow.AddMinutes(-1);
        store.Update(config => config with
        {
            BlockedCommands =
            [
                new BlockedCommandRule { Pattern = "git checkout" },
                new BlockedCommandRule { Pattern = "git push" },
            ],
        });

        var config = store.ToggleBlockedCommand("git checkout", false);

        var checkout = config.BlockedCommands.Single(rule => rule.Pattern == "git checkout");
        var push = config.BlockedCommands.Single(rule => rule.Pattern == "git push");
        Assert.False(checkout.Enabled);
        Assert.True(push.Enabled);
        Assert.NotNull(checkout.UpdatedAt);
        Assert.True(checkout.UpdatedAt >= before);
        Assert.Null(push.UpdatedAt);
        Assert.True(config.UpdatedAt >= before);

        var persisted = NewStore().GetConfig();
        Assert.False(persisted.BlockedCommands.Single(rule => rule.Pattern == "git checkout").Enabled);
        Assert.True(persisted.BlockedCommands.Single(rule => rule.Pattern == "git push").Enabled);
    }

    /// <summary>
    /// Directory paths are matched the way the tool matches them later:
    /// case-insensitively, so a toggle sent from the dashboard for
    /// <c>c:\shared</c> still reaches the rule stored as <c>C:\Shared</c>.
    /// </summary>
    [Fact]
    public void ToggleAllowedDirectory_FlipsOnlyTheMatchingRule()
    {
        var store = NewStore();
        store.Update(config => config with
        {
            AllowedDirectories =
            [
                new AllowedDirectoryRule { Path = "C:\\Shared" },
                new AllowedDirectoryRule { Path = "D:\\Scratch" },
            ],
        });

        var before = store.GetConfig().UpdatedAt;
        var config = store.ToggleAllowedDirectory("c:\\shared", false);

        Assert.False(config.AllowedDirectories.Single(rule => rule.Path == "C:\\Shared").Enabled);
        Assert.True(config.AllowedDirectories.Single(rule => rule.Path == "D:\\Scratch").Enabled);
        Assert.True(config.UpdatedAt >= before);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best effort cleanup.
        }
    }
}
