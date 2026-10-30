namespace Eling.Core.Workspaces;

/// <summary>
/// How a workspace list is arranged for display. The integer values are persisted
/// by the desktop settings store, so the order is part of the contract.
/// </summary>
public enum WorkspaceGroupMode
{
    Flat = 0,
    Repo = 1,
    Folder = 2,
}
