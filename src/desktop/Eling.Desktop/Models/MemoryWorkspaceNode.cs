using System.Collections.Generic;
using System.Linq;
using Avalonia;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace Eling.Desktop.Models;

/// <summary>One visible entry in the Memory workspace tree.</summary>
public sealed class MemoryWorkspaceNode : ReactiveObject
{
    private bool _isExpanded;
    private bool? _checkState;
    private int _depth;

    public MemoryWorkspaceNode(
        string id,
        string label,
        string path,
        WorkspaceItem? row,
        IReadOnlyList<WorkspaceItem> group,
        bool isWorktree,
        bool isExpanded,
        IReadOnlyList<MemoryWorkspaceNode> children)
    {
        Id = id;
        Label = label;
        Path = path;
        Row = row;
        Group = group;
        IsWorktree = isWorktree;
        _isExpanded = isExpanded;
        Children = children;
        RefreshAggregate();
    }

    public string Id { get; }
    public string Label { get; }
    public string Path { get; }
    public WorkspaceItem? Row { get; }
    public IReadOnlyList<WorkspaceItem> Group { get; }
    public IReadOnlyList<MemoryWorkspaceNode> Children { get; }
    public bool IsWorktree { get; }
    public bool HasChildren => Children.Count > 0;
    public bool CanSelectOnly => Row is not null;
    public bool ShowGroupCount => Group.Count > 1;
    public string GroupCountText => Group.Count.ToString();
    public string ChevronGlyph => IsExpanded ? "▾" : "▸";
    public Thickness RowMargin => new(Depth * 12, 1, 0, 1);
    public int Depth => _depth;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            this.RaiseAndSetIfChanged(ref _isExpanded, value);
            this.RaisePropertyChanged(nameof(ChevronGlyph));
        }
    }

    public bool? CheckState
    {
        get => _checkState;
        private set => this.RaiseAndSetIfChanged(ref _checkState, value);
    }

    public bool IsAlive => Group.Any(workspace => workspace.IsAlive);
    public string DotColor => IsAlive ? "#10b981" : "#4b5563";
    public string StatusTip => IsAlive ? "Runtime is live" : "Offline — or liveness unknown";

    public void RefreshAggregate()
    {
        var selected = Group.Count(workspace => workspace.IsSelected);
        CheckState = selected == 0 ? false : selected == Group.Count ? true : null;
        this.RaisePropertyChanged(nameof(IsAlive));
        this.RaisePropertyChanged(nameof(DotColor));
        this.RaisePropertyChanged(nameof(StatusTip));
    }

    internal void SetDepth(int depth)
    {
        this.RaiseAndSetIfChanged(ref _depth, depth);
        this.RaisePropertyChanged(nameof(RowMargin));
    }
}
