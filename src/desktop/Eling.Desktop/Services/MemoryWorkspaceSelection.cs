using System.Collections.Generic;
using System.Linq;
using Eling.Desktop.Models;

namespace Eling.Desktop.Services;

/// <summary>Selection operations shared by the Memory workspace rows.</summary>
public static class MemoryWorkspaceSelection
{
    public static void SetAll(IEnumerable<WorkspaceItem> workspaces, bool selected)
    {
        foreach (var workspace in workspaces) workspace.IsSelected = selected;
    }

    public static void SelectOnly(IEnumerable<WorkspaceItem> workspaces, WorkspaceItem selected)
    {
        foreach (var workspace in workspaces)
            workspace.IsSelected = ReferenceEquals(workspace, selected);
    }

    /// <summary>
    /// A partially selected group becomes complete on the next toggle.
    /// </summary>
    public static void ToggleGroup(IReadOnlyCollection<WorkspaceItem> group)
    {
        if (group.Count == 0) return;
        var allSelected = group.All(workspace => workspace.IsSelected);
        foreach (var workspace in group) workspace.IsSelected = !allSelected;
    }
}
