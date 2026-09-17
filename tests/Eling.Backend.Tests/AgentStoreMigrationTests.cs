using Eling.Backend.Agent.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eling.Backend.Tests;

public class AgentStoreMigrationTests : IDisposable
{
    private readonly string _tempDir;

    public AgentStoreMigrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "eling-mig-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void ProviderStore_MovesLegacyFileToGlobal()
    {
        var legacyDir = Path.Combine(_tempDir, "legacy");
        var globalDir = Path.Combine(_tempDir, "global");
        Directory.CreateDirectory(legacyDir);
        File.WriteAllText(
            Path.Combine(legacyDir, "agent-provider.json"),
            """{"BaseUrl":"http://x/v1","Model":"m1","ApiKey":"k","ModelsCached":[]}""");

        var store = new ProviderStore(globalDir, NullLogger<ProviderStore>.Instance, legacyDir);

        Assert.True(store.TryGetConfig(out var baseUrl, out _, out _));
        Assert.Equal("http://x/v1", baseUrl);
        Assert.True(File.Exists(Path.Combine(globalDir, "agent-provider.json")));
        Assert.False(File.Exists(Path.Combine(legacyDir, "agent-provider.json")));
    }

    [Fact]
    public void WorkspaceRegistry_MovesLegacyFileToGlobal()
    {
        var legacyDir = Path.Combine(_tempDir, "legacy");
        var globalDir = Path.Combine(_tempDir, "global");
        Directory.CreateDirectory(legacyDir);
        File.WriteAllText(Path.Combine(legacyDir, "agent-workspaces.json"), """["C:\\work"]""");

        var registry = new WorkspaceRegistry(
            Path.Combine(globalDir, "agent-workspaces.json"),
            NullLogger<WorkspaceRegistry>.Instance,
            Path.Combine(legacyDir, "agent-workspaces.json"));

        Assert.Contains("C:\\work", registry.List());
        Assert.False(File.Exists(Path.Combine(legacyDir, "agent-workspaces.json")));
    }

    [Fact]
    public void ChatStore_MovesLegacyDirectoryToGlobal()
    {
        var legacyDir = Path.Combine(_tempDir, "legacy", "agent-chats");
        var globalDir = Path.Combine(_tempDir, "global", "agent-chats");
        Directory.CreateDirectory(legacyDir);
        File.WriteAllText(Path.Combine(legacyDir, "dummy.json"), "{}");

        _ = new BackendChatStore(globalDir, NullLogger<BackendChatStore>.Instance, legacyDir);

        Assert.True(Directory.Exists(globalDir));
        Assert.False(Directory.Exists(legacyDir));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
        }
    }
}
