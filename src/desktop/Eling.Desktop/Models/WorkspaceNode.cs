using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace Eling.Desktop.Models;

public sealed class WorkspaceNode : ReactiveObject
{
    private bool _isExpanded = true;

    public string Id { get; }
    public string Label { get; }
    public string Path { get; }
    public WorkspaceItemDto? Row { get; }
    public IReadOnlyList<WorkspaceItemDto> Group { get; }
    public bool IsRepository { get; }
    public bool IsWorktree { get; }
    public ObservableCollection<WorkspaceNode> Children { get; } = new();

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            this.RaiseAndSetIfChanged(ref _isExpanded, value);
            this.RaisePropertyChanged(nameof(ChevronGlyph));
        }
    }

    public string ChevronGlyph => IsExpanded ? "▾" : "▸";

    public WorkspaceNode(
        string id,
        string label,
        string path,
        WorkspaceItemDto? row,
        IReadOnlyList<WorkspaceItemDto> group,
        bool isRepository,
        bool isWorktree)
    {
        Id = id;
        Label = label;
        Path = path;
        Row = row;
        Group = group;
        IsRepository = isRepository;
        IsWorktree = isWorktree;
    }
}