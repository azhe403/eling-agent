using System.Xml.Linq;

namespace Eling.Host.Tests;

/// <summary>
/// Pecut 9 binary structure invariants: exactly two executables (eling,
/// eling-dashboard); everything else stays a library.
/// </summary>
public sealed class BinaryStructureTests
{
    private static string RepoRoot => Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static XDocument LoadProject(string relativePath) =>
        XDocument.Load(Path.Combine(RepoRoot, relativePath));

    private static string? OutputType(string projectPath) =>
        LoadProject(projectPath).Root?
            .Element("PropertyGroup")?
            .Element("OutputType")?
            .Value;

    private static string? AssemblyName(string projectPath) =>
        LoadProject(projectPath).Root?
            .Elements("PropertyGroup")
            .Select(g => g.Element("AssemblyName")?.Value)
            .FirstOrDefault(v => v is not null);

    [Fact]
    public void Eling_Host_is_executable_named_eling()
    {
        Assert.Equal("Exe", OutputType("src/backend/Eling.Host/Eling.Host.csproj"));
        Assert.Equal("eling", AssemblyName("src/backend/Eling.Host/Eling.Host.csproj"));
    }

    [Fact]
    public void Eling_Dashboard_is_executable_named_eling_dashboard()
    {
        Assert.Equal("Exe", OutputType("src/backend/Eling.Dashboard/Eling.Dashboard.csproj"));
        Assert.Equal("eling-dashboard", AssemblyName("src/backend/Eling.Dashboard/Eling.Dashboard.csproj"));
    }

    [Theory]
    [InlineData("src/backend/Eling.Core/Eling.Core.csproj")]
    [InlineData("src/backend/Eling.Application/Eling.Application.csproj")]
    [InlineData("src/backend/Eling.Mcp/Eling.Mcp.csproj")]
    public void Shared_projects_remain_libraries(string projectPath)
    {
        // No OutputType element defaults to Library; an explicit Exe would
        // violate the two-executable rule.
        var explicitOutputType = LoadProject(projectPath).Root?
            .Elements("PropertyGroup")
            .Select(g => g.Element("OutputType")?.Value)
            .FirstOrDefault(v => v is not null);

        Assert.True(explicitOutputType is null or "Library",
            $"{projectPath} must remain a library.");
    }
}
