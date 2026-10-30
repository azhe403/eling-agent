using Eling.Desktop.Services;
using Xunit;

namespace Eling.Desktop.Tests;

/// <summary>
/// The workspace tree's open/closed shape, as the desktop and the dashboard
/// share it. The backend store is the source of truth; this type is the shape
/// both sides speak.
/// </summary>
public class WorkspaceExpansionStateTests
{
    [Fact]
    public void IsExpanded_WithoutAChoice_KeepsTheDefault()
    {
        var state = new WorkspaceExpansionState();

        Assert.True(state.IsExpanded("node", defaultOpen: true));
        Assert.False(state.IsExpanded("node", defaultOpen: false));
    }

    [Fact]
    public void Set_Open_MarksExpandedAndClearsCollapsed()
    {
        var state = new WorkspaceExpansionState(expanded: [], collapsed: ["node"]);

        state.Set("node", expanded: true);

        Assert.True(state.IsExpanded("node", defaultOpen: false));
        Assert.DoesNotContain("node", state.Collapsed);
    }

    [Fact]
    public void Set_Closed_MarksCollapsedAndClearsExpanded()
    {
        var state = new WorkspaceExpansionState(expanded: ["node"], collapsed: []);

        state.Set("node", expanded: false);

        Assert.False(state.IsExpanded("node", defaultOpen: true));
        Assert.DoesNotContain("node", state.Expanded);
    }

    [Fact]
    public void Replace_SwapsTheWholeState()
    {
        var state = new WorkspaceExpansionState(expanded: ["old"], collapsed: ["stale"]);

        state.Replace(["fresh"], ["closed"]);

        Assert.Equal(["fresh"], state.Expanded);
        Assert.Equal(["closed"], state.Collapsed);
    }

    [Fact]
    public void ToLists_ReturnsBothSets()
    {
        var state = new WorkspaceExpansionState(expanded: ["a"], collapsed: ["b"]);

        var (expanded, collapsed) = state.ToLists();

        Assert.Equal(["a"], expanded);
        Assert.Equal(["b"], collapsed);
    }
}
