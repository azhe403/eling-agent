using Eling.Backend.Mcp;
using Eling.Backend.Tools;
using Xunit;

namespace Eling.Backend.Tests;

public sealed class DocsInstructionTests
{
    [Fact]
    public void ServerInstructions_DescribeUnifiedDocs()
    {
        var text = ServerInstructions.Default;

        Assert.Contains("unified catalog", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("managed or external", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".eling/docs/", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("promote: true", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DocToolDescriptions_UseSqliteMetadata()
    {
        var save = Assert.Single(ToolCatalog.All, tool => tool.Name == "doc_save");
        var annotate = Assert.Single(ToolCatalog.All, tool => tool.Name == "doc_annotate");

        Assert.Contains("SQLite", save.Description, StringComparison.Ordinal);
        Assert.Contains("SQLite", annotate.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("JSON metadata", save.Description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("JSON metadata", annotate.Description, StringComparison.OrdinalIgnoreCase);
    }
}
