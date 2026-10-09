using Eling.Backend.Tools;
using Eling.Core.Scope;
using Eling.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eling.Backend.Tests.Tools;

/// <summary>
/// Tool policy is per-user runtime configuration, so these tests verify
/// persistence round-trips, protected tool immunity, and graceful
/// degradation on corrupt files.
/// </summary>
public sealed class ToolPolicyStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"eling-tools-{Guid.NewGuid():N}");

    private ToolPolicyStore NewStore()
    {
        Directory.CreateDirectory(_root);
        return new ToolPolicyStore(new UserScope(_root), NullLogger<ToolPolicyStore>.Instance);
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

    [Fact]
    public void FreshStore_HasNoDisabledTools()
    {
        var store = NewStore();

        Assert.Empty(store.GetDisabledTools());
        Assert.False(store.IsToolDisabled("file_delete"));
    }

    [Fact]
    public void DisableTools_AddsToolsAndSetsTimestampFromCode()
    {
        var store = NewStore();
        var before = DateTimeOffset.UtcNow.AddMinutes(-1);

        var config = store.DisableTools(["file_delete", "directory_delete"]);

        Assert.True(store.IsToolDisabled("file_delete"));
        Assert.True(store.IsToolDisabled("directory_delete"));
        Assert.Contains("file_delete", config.DisabledTools);
        Assert.True(config.UpdatedAt >= before);
        Assert.True(config.UpdatedAt <= DateTimeOffset.UtcNow.AddMinutes(1));
    }

    [Fact]
    public void DisableTools_ProtectedToolsAreIgnored()
    {
        var store = NewStore();

        var config = store.DisableTools(["tools_policy", "memory_recall", "file_delete"]);

        Assert.False(store.IsToolDisabled("tools_policy"));
        Assert.False(store.IsToolDisabled("memory_recall"));
        Assert.True(store.IsToolDisabled("file_delete"));
        Assert.DoesNotContain("tools_policy", config.DisabledTools);
        Assert.DoesNotContain("memory_recall", config.DisabledTools);
    }

    [Fact]
    public void EnableTools_RemovesToolsFromDisabledSet()
    {
        var store = NewStore();
        store.DisableTools(["file_delete", "file_edit"]);

        store.EnableTools(["file_delete"]);

        Assert.False(store.IsToolDisabled("file_delete"));
        Assert.True(store.IsToolDisabled("file_edit"));
    }

    [Fact]
    public void Reset_ClearsAllDisabledTools()
    {
        var store = NewStore();
        store.DisableTools(["file_delete", "file_edit"]);

        store.Reset();

        Assert.Empty(store.GetDisabledTools());
    }

    [Fact]
    public void Policy_SurvivesANewStoreInstance()
    {
        NewStore().DisableTools(["file_delete"]);

        Assert.True(NewStore().IsToolDisabled("file_delete"));
    }

    [Fact]
    public void ConfigFile_UsesSnakeCaseFields()
    {
        NewStore().DisableTools(["file_delete"]);

        var raw = File.ReadAllText(Path.Combine(_root, "config", "tools-policy.json"));

        Assert.Contains("\"disabled_tools\"", raw);
        Assert.Contains("\"updated_at\"", raw);
    }

    [Fact]
    public void CorruptConfigFile_DegradesToEmptyInsteadOfThrowing()
    {
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        File.WriteAllText(Path.Combine(_root, "config", "tools-policy.json"), "{ not json");

        var store = NewStore();

        Assert.Empty(store.GetDisabledTools());
    }

    [Fact]
    public void CorruptConfig_IsNotOverwrittenSoItCanBeInspected()
    {
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        var path = Path.Combine(_root, "config", "tools-policy.json");
        File.WriteAllText(path, "{ not json");

        _ = NewStore();

        Assert.Equal("{ not json", File.ReadAllText(path));
    }

    [Fact]
    public void ConfigFile_LivesInUserConfigDirectory()
    {
        NewStore().DisableTools(["file_delete"]);

        Assert.True(File.Exists(Path.Combine(_root, "config", "tools-policy.json")));
    }
}
