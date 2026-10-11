using System.Text.Json;
using Eling.Core.Matching;
using Eling.Core.Runtime;
using Eling.Core.Serialization;
using Eling.Core.Tools;

namespace Eling.Core.Tests;

public sealed class InfraValueTests
{
    [Theory]
    [InlineData("*.cs", "foo.cs", true)]
    [InlineData("*.cs", "dir/foo.cs", false)]
    [InlineData("**/*.cs", "dir/sub/foo.cs", true)]
    [InlineData("foo?.cs", "foox.cs", true)]
    [InlineData("foo?.cs", "foo.cs", false)]
    [InlineData("*.cs", "FOO.CS", true)]
    public void Glob_IsMatch_Matrix(string glob, string path, bool expected)
    {
        // Act
        var actual = GlobPattern.IsMatch(glob, path);

        // Assert
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Glob_NormalizesSeparators()
    {
        // Act & Assert — backslashes treated as slashes on both sides
        Assert.True(GlobPattern.IsMatch("**/*.cs", @"dir\sub\foo.cs"));
        Assert.True(GlobPattern.IsMatch(@"dir\*.cs", "dir/foo.cs"));
    }

    [Theory]
    [InlineData(null, "foo.cs")]
    [InlineData("*.cs", null)]
    public void Glob_ThrowsArgumentNullOnNull(string? glob, string? path)
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => GlobPattern.IsMatch(glob!, path!));
    }

    [Theory]
    [InlineData("", "foo.cs")]
    [InlineData("*.cs", "")]
    public void Glob_ThrowsOnEmpty(string? glob, string? path)
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => GlobPattern.IsMatch(glob!, path!));
    }

    [Fact]
    public void JsonDefaults_SharedOptions()
    {
        // Assert — camelCase, case-insensitive, indented
        Assert.Equal(JsonNamingPolicy.CamelCase, JsonDefaults.Shared.PropertyNamingPolicy);
        Assert.True(JsonDefaults.Shared.PropertyNameCaseInsensitive);
        Assert.True(JsonDefaults.Shared.WriteIndented);
    }

    [Fact]
    public void JsonDefaults_RoundTripsCamelCase()
    {
        // Arrange
        var value = new ToolPolicyConfig
        {
            DisabledTools = ["my-tool"],
            UpdatedAt = new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero)
        };

        // Act
        var json = JsonSerializer.Serialize(value, JsonDefaults.Shared);

        // Assert
        Assert.Contains("disabledTools", json);
        Assert.Contains("updatedAt", json);
        var back = JsonSerializer.Deserialize<ToolPolicyConfig>(json, JsonDefaults.Shared);
        Assert.NotNull(back);
        Assert.Equal(value.DisabledTools, back.DisabledTools);
    }

    [Fact]
    public void ToolPolicy_Defaults()
    {
        // Act
        var config = new ToolPolicyConfig();

        // Assert
        Assert.NotNull(config.DisabledTools);
        Assert.Empty(config.DisabledTools);
        Assert.Equal(default, config.UpdatedAt);
    }

    [Fact]
    public void RuntimeInfo_CodebaseRoot_FallsBackToHead()
    {
        // Arrange
        var legacy = new RuntimeInfo { HeadScopeRoot = "/head", WorkspaceRoot = "" };
        var current = new RuntimeInfo { HeadScopeRoot = "/head", WorkspaceRoot = "/work" };

        // Act & Assert
        Assert.Equal("/head", legacy.CodebaseRoot());
        Assert.Equal("/work", current.CodebaseRoot());
    }

    [Fact]
    public void RuntimeInfo_Defaults()
    {
        // Act
        var info = new RuntimeInfo();

        // Assert
        Assert.Equal("", info.HeadScopeRoot);
        Assert.Equal("", info.WorkspaceRoot);
        Assert.True(info.CodebaseEnabled);
        Assert.True(info.IsAlive);
        Assert.Equal("", info.DataDirectory);
        Assert.Equal("", info.McpTransport);
    }

    [Fact]
    public void RuntimeRegistration_Defaults()
    {
        // Act
        var registration = new RuntimeRegistration();

        // Assert
        Assert.Equal(0, registration.ProcessId);
        Assert.Equal("", registration.HeadScopeRoot);
        Assert.Equal("", registration.WorkspaceRoot);
        Assert.True(registration.CodebaseEnabled);
        Assert.False(registration.McpEnabled);
    }
}
