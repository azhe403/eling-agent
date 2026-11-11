using System.Reflection;
using Eling.Backend.Mcp.Tools;
using ModelContextProtocol.Server;
using Xunit;

namespace Eling.Backend.Tests.Shell;

/// <summary>
/// Pins the MCP discovery surface of <see cref="ShellTools"/>: the server
/// registers tools by scanning the assembly, so the type attribute is what puts
/// <c>shell_execute</c> in the tool list and the attribute name is what a client
/// calls. A rename here silently removes the tool instead of failing to build.
/// </summary>
public sealed class ShellToolsRegistrationTests
{
    [Fact]
    public void ShellTools_IsMcpServerToolType()
    {
        var attribute = typeof(ShellTools).GetCustomAttribute<McpServerToolTypeAttribute>();

        Assert.NotNull(attribute);
    }

    [Fact]
    public void ExecuteAsync_IsRegisteredAsShellExecute()
    {
        var attribute = typeof(ShellTools)
            .GetMethod(nameof(ShellTools.ExecuteAsync))
            ?.GetCustomAttribute<McpServerToolAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal("shell_execute", attribute!.Name);
    }
}
