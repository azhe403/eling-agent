using Eling.Desktop.Models;

namespace Eling.Desktop.Tests;

/// <summary>
/// A single chat history can carry megabytes of tool output. Building the JSON tree
/// for every row while loading it, with every row expanded, is what pushed the
/// window past 4 GB and stopped it responding, so the tree is built on demand.
/// </summary>
public class ChatMessageRowTests
{
    private const string JsonPayload = """{"alpha":1,"beta":{"gamma":[1,2,3]}}""";

    [Fact]
    public void CollapsedRow_DoesNotBuildTheTree()
    {
        var row = new ChatMessageRow("Tool", JsonPayload, "read_file", startsExpanded: false);

        Assert.False(row.IsExpanded);
        Assert.True(row.HasTree);
        Assert.Empty(row.TreeNodes);
    }

    [Fact]
    public void ExpandingTheRow_BuildsTheTreeOnDemand()
    {
        var row = new ChatMessageRow("Tool", JsonPayload, "read_file", startsExpanded: false);

        row.ToggleExpand();

        Assert.True(row.IsExpanded);
        Assert.NotEmpty(row.TreeNodes);
    }

    [Fact]
    public void RowBuiltExpanded_AlreadyHasItsTree()
    {
        var row = new ChatMessageRow("Tool", JsonPayload, "read_file");

        Assert.True(row.IsExpanded);
        Assert.NotEmpty(row.TreeNodes);
    }

    [Fact]
    public void AssistantRow_HasNoTree()
    {
        var row = new ChatMessageRow("Assistant", "here is `code`", null);

        Assert.False(row.HasTree);
        Assert.Empty(row.TreeNodes);
    }

    [Fact]
    public void ErrorOutput_IsNotOfferedAsATree()
    {
        var row = new ChatMessageRow("Tool", "Error: file not found", "read_file");

        Assert.False(row.HasTree);
        Assert.Empty(row.TreeNodes);
    }
}
