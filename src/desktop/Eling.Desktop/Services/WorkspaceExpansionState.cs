using System;
using System.Collections.Generic;
using System.Linq;

namespace Eling.Desktop.Services;

/// <summary>
/// Which workspace-tree nodes the user has opened and which ones they have
/// closed. The same shape the backend stores, so the desktop and the dashboard
/// share one tree.
/// </summary>
/// <remarks>
/// A node the user never touched keeps the default for its kind — a repository
/// with several checkouts starts open, everything else starts closed. That is
/// why the state is two sets rather than one map: "closed" has to be
/// remembered too, or a collapsed repository reopens on the next visit.
/// </remarks>
public sealed class WorkspaceExpansionState
{
    private readonly HashSet<string> _expanded;
    private readonly HashSet<string> _collapsed;

    public WorkspaceExpansionState(
        IEnumerable<string>? expanded = null,
        IEnumerable<string>? collapsed = null)
    {
        _expanded = new HashSet<string>(expanded ?? [], StringComparer.Ordinal);
        _collapsed = new HashSet<string>(collapsed ?? [], StringComparer.Ordinal);
    }

    /// <summary>The node ids the user explicitly opened.</summary>
    public IReadOnlyCollection<string> Expanded => _expanded;

    /// <summary>The node ids the user explicitly closed.</summary>
    public IReadOnlyCollection<string> Collapsed => _collapsed;

    /// <summary>
    /// Whether a node shows its children. An explicit choice wins; otherwise the
    /// node's default decides.
    /// </summary>
    public bool IsExpanded(string nodeId, bool defaultOpen)
    {
        if (_expanded.Contains(nodeId)) return true;
        if (_collapsed.Contains(nodeId)) return false;
        return defaultOpen;
    }

    /// <summary>
    /// Records the user opening or closing a node, replacing the opposite
    /// choice for that node.
    /// </summary>
    public void Set(string nodeId, bool expanded)
    {
        if (expanded)
        {
            _expanded.Add(nodeId);
            _collapsed.Remove(nodeId);
            return;
        }
        _collapsed.Add(nodeId);
        _expanded.Remove(nodeId);
    }

    /// <summary>
    /// Replaces the whole state, as the shared backend store delivers it.
    /// </summary>
    public void Replace(IEnumerable<string>? expanded, IEnumerable<string>? collapsed)
    {
        _expanded.Clear();
        _collapsed.Clear();
        foreach (var id in expanded ?? []) _expanded.Add(id);
        foreach (var id in collapsed ?? []) _collapsed.Add(id);
    }

    /// <summary>The state as the backend stores it.</summary>
    public (IReadOnlyList<string> Expanded, IReadOnlyList<string> Collapsed) ToLists() =>
        (_expanded.ToList(), _collapsed.ToList());
}
