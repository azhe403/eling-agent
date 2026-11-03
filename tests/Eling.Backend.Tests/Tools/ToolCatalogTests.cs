using System.Reflection;
using Eling.Backend.Tools;
using ModelContextProtocol.Server;
using Xunit;

namespace Eling.Backend.Tests.Tools;

/// <summary>
/// Regression test ensuring that every MCP tool exposed with [McpServerTool]
/// in the Eling.Backend assembly is registered in <see cref="ToolCatalog.All"/>,
/// and that no tool or group is misspelled or orphaned.
/// </summary>
public sealed class ToolCatalogTests
{
    [Fact]
    public void Catalog_ContainsEveryRegisteredMcpServerTool()
    {
        var backendAssembly = typeof(ToolCatalog).Assembly;

        var registeredToolNames = backendAssembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>())
            .Where(attr => attr is not null)
            .Select(attr => attr!.Name ?? string.Empty)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        var catalogNames = ToolCatalog.All
            .Select(t => t.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(registeredToolNames);
        Assert.Equal(registeredToolNames, catalogNames);
    }

    [Fact]
    public void Catalog_HasNoDuplicateToolNames()
    {
        var duplicates = ToolCatalog.All
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.Empty(duplicates);
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("codebase")]
    [InlineData("filesystem")]
    [InlineData("diagnostics")]
    [InlineData("database")]
    public void MembersOf_KnownGroups_ReturnNonEmptyToolLists(string group)
    {
        var members = ToolCatalog.MembersOf(group);
        Assert.NotEmpty(members);
    }

    [Fact]
    public void AllTools_BelongToKnownGroups()
    {
        var knownGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ToolCatalog.MemoryGroup,
            ToolCatalog.CodebaseGroup,
            ToolCatalog.FilesystemGroup,
            ToolCatalog.DiagnosticsGroup,
            ToolCatalog.DatabaseGroup,
        };

        foreach (var tool in ToolCatalog.All)
        {
            Assert.Contains(tool.Group, knownGroups);
            Assert.False(string.IsNullOrWhiteSpace(tool.Description));
        }
    }
}
