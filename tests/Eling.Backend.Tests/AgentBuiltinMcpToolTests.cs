using System.Text.Json;
using Eling.Backend.Agent.Ports;
using Eling.Backend.Agent.Services.Tools;
using Eling.Backend.Mcp;
using Eling.Core.Scope;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Eling.Backend.Tests;

public sealed class AgentBuiltinMcpToolTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ServiceProvider _provider;

    public AgentBuiltinMcpToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "eling-agent-tools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var services = new ServiceCollection();
        var userScope = UserScope.Resolve(Path.Combine(_tempDir, "user-scope"));
        var projectScope = new ProjectScope(_tempDir);
        var chain = new ScopeChain(_tempDir, [projectScope]);

        services.AddLogging();
        services.AddElingCoreServices(chain, userScope);
        services.AddElingAgentTools();

        _provider = services.BuildServiceProvider();
    }

    [Fact]
    public void Tools_AreRegisteredInDi()
    {
        using var scope = _provider.CreateScope();
        var tools = scope.ServiceProvider.GetServices<IAgentTool>().ToList();

        Assert.Equal(8, tools.Count);
        Assert.Contains(tools, t => t.Name == "memory_recall");
        Assert.Contains(tools, t => t.Name == "memory_save");
        Assert.Contains(tools, t => t.Name == "memory_search");
        Assert.Contains(tools, t => t.Name == "file_read");
        Assert.Contains(tools, t => t.Name == "file_write");
        Assert.Contains(tools, t => t.Name == "directory_list");
        Assert.Contains(tools, t => t.Name == "glob");
        Assert.Contains(tools, t => t.Name == "file_search");
    }

    [Fact]
    public async Task FileTools_WriteAndRead_Succeeds()
    {
        using var scope = _provider.CreateScope();
        var writeTool = scope.ServiceProvider.GetServices<IAgentTool>().First(t => t.Name == "file_write");
        var readTool = scope.ServiceProvider.GetServices<IAgentTool>().First(t => t.Name == "file_read");

        var writeArgs = JsonSerializer.Serialize(new
        {
            path = "hello.txt",
            content = "Hello built-in MCP!",
            overwrite = true
        });

        var writeOutput = await writeTool.ExecuteAsync(writeArgs, CancellationToken.None);
        var writeDoc = JsonDocument.Parse(writeOutput);
        Assert.True(writeDoc.RootElement.GetProperty("sizeBytes").GetInt32() > 0);

        var readArgs = JsonSerializer.Serialize(new
        {
            path = "hello.txt"
        });

        var readOutput = await readTool.ExecuteAsync(readArgs, CancellationToken.None);
        Assert.Contains("Hello built-in MCP!", readOutput);
    }

    [Fact]
    public async Task MemoryRecallTool_Executes_ReturnsValidJson()
    {
        using var scope = _provider.CreateScope();
        var recallTool = scope.ServiceProvider.GetServices<IAgentTool>().First(t => t.Name == "memory_recall");

        var output = await recallTool.ExecuteAsync("{}", CancellationToken.None);
        var doc = JsonDocument.Parse(output);

        Assert.True(doc.RootElement.TryGetProperty("recallMemories", out _));
        Assert.True(doc.RootElement.TryGetProperty("recentMemories", out _));
        Assert.True(doc.RootElement.TryGetProperty("stats", out _));
    }

    public void Dispose()
    {
        _provider.Dispose();
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
