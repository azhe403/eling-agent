using System.ComponentModel;
using System.Linq;
using Eling.Desktop.Models;

namespace Eling.Desktop.Tests;

/// <summary>
/// The tree bound into ChatView.axaml. The notification contract matters more than
/// the parsing: a raise under the wrong property name compiles, passes every other
/// test, and leaves a collapsed tree that simply never opens.
/// </summary>
public class JsonTreeNodeTests
{
    private static List<string> TrackChanges(JsonTreeNode node)
    {
        var changed = new List<string>();
        ((INotifyPropertyChanged)node).PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");
        return changed;
    }

    [Fact]
    public void Toggle_AnnouncesIsExpandedAndChevron()
    {
        var node = new JsonTreeNode("root", null, [new JsonTreeNode("leaf", "v", [])], alwaysExpanded: true);
        Assert.True(node.IsExpanded);
        Assert.Equal("▾", node.Chevron);

        var changed = TrackChanges(node);
        node.Toggle();

        Assert.False(node.IsExpanded);
        Assert.Equal("▸", node.Chevron);
        // Both names, and specifically "IsExpanded" rather than "Toggle": the
        // raise lives in the setter so CallerMemberName resolves correctly. Moving
        // it into Toggle() would announce "Toggle" and the binding would go quiet.
        Assert.Contains("IsExpanded", changed);
        Assert.Contains("Chevron", changed);
    }

    [Fact]
    public void Toggle_OnALeaf_StillFlipsTheFlag()
    {
        var leaf = new JsonTreeNode("k", "v", []);

        leaf.Toggle();

        Assert.True(leaf.IsExpanded);
        // A leaf has nothing to reveal, so it must not draw a chevron either way.
        Assert.Equal("", leaf.Chevron);
    }

    [Fact]
    public void WideBranch_StartsCollapsed_SoTheTreeStaysReadable()
    {
        var many = Enumerable.Range(0, 20).Select(i => new JsonTreeNode($"[{i}]", "v", [])).ToList();
        var node = new JsonTreeNode("root", null, many);

        Assert.False(node.IsExpanded);
    }

    [Fact]
    public void Parse_InvalidJson_KeepsTheRowUsableAsARawLeaf()
    {
        var node = Assert.Single(JsonTreeNode.Parse("Error: file not found"));

        Assert.Equal("(raw)", node.Label);
        Assert.Equal("Error: file not found", node.Value);
        Assert.True(node.IsLeaf);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_NothingToShow_ProducesNoNodes(string json)
    {
        Assert.Empty(JsonTreeNode.Parse(json));
    }

    [Fact]
    public void Parse_AutoExpand_OpensTheFirstLevelsButStopsAtAWideBranch()
    {
        var wide = string.Join(",", Enumerable.Range(0, 20).Select(i => $"\"k{i}\":{i}"));
        var root = Assert.Single(JsonTreeNode.Parse($"{{\"a\":{{\"b\":{{{wide}}}}},\"d\":2}}"));

        // The rule is "has children AND (first two levels OR narrow)", not a plain
        // depth limit — a two-child branch stays open however deep it sits.
        Assert.True(root.IsExpanded);

        var a = root.Children.Single(c => c.Label == "a");
        Assert.True(a.IsExpanded);

        var b = Assert.Single(a.Children);
        Assert.False(b.IsExpanded);
        Assert.Equal(20, b.Children.Count);

        var d = root.Children.Single(c => c.Label == "d");
        Assert.True(d.IsLeaf);
        Assert.Equal("", d.Chevron);
    }
}
