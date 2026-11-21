using System;
using System.Collections.Generic;
using Eling.Desktop.Formatting;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace Eling.Desktop.Models;

/// <summary>
/// One rendered turn in the transcript: a user message, an assistant reply, or a
/// tool call. Owns its own expand state and builds its JSON tree lazily, because a
/// single history can hold megabytes of tool output and materialising every row up
/// front is what stopped the window responding.
/// </summary>
public class ChatMessageRow : ReactiveObject
{
    private bool _isExpanded;
    private bool _isCodeMode;
    private readonly string? _treeSource;
    private IReadOnlyList<JsonTreeNode>? _treeNodes;

    public string Role { get; }
    public string Text { get; }
    public string? ToolName { get; }
    public string? Arguments { get; }

    public bool IsUser => string.Equals(Role, "User", StringComparison.OrdinalIgnoreCase);
    public bool IsAssistant => string.Equals(Role, "Assistant", StringComparison.OrdinalIgnoreCase);
    public bool IsTool => string.Equals(Role, "Tool", StringComparison.OrdinalIgnoreCase);

    /// <summary>User rows are chat bubbles aligned right; everything else stretches.</summary>
    public Avalonia.Layout.HorizontalAlignment RowHorizontalAlignment =>
        IsUser ? Avalonia.Layout.HorizontalAlignment.Right : Avalonia.Layout.HorizontalAlignment.Stretch;

    public double RowMaxWidth => IsUser ? 560 : double.PositiveInfinity;

    public double RowMinWidth => IsUser ? 180 : 0;

    /// <summary>Body visibility mirrors IsExpanded; the waiting state lives in the bottom busy bar, not in a row.</summary>
    public bool ShowBody => IsExpanded;
    public bool IsToolError => IsTool && ToolOutputFormatter.IsError(Text);

    public string HeaderDisplay => IsUser ? "You" : IsAssistant ? "Eling" : $"🔧 Tool: {ToolName ?? "Execution"}";
    public string HeaderColor => IsUser ? "#2563eb" : IsAssistant ? "#16a34b" : IsToolError ? "#dc2626" : "#d97706";
    public string TextColor => IsTool ? (IsToolError ? "#fca5a5" : "#cbd5e1") : "#f8fafc";
    public string FontFamily => IsTool ? "Cascadia Code,Consolas,Menlo,monospace" : "Inter,Segoe UI,sans-serif";
    public double FontSize => IsTool ? 12.0 : 13.5;

    public string ArgumentsDisplay => ToolOutputFormatter.FormatArguments(Arguments);
    public bool HasArguments => !string.IsNullOrWhiteSpace(ArgumentsDisplay);

    /// <summary>True when this row carries a JSON payload worth showing as a tree.</summary>
    public bool HasTree => _treeSource is not null;

    /// Navigable JSON tree: the full payload, collapsible at every level. Parsed on
    /// first expand rather than at construction, because one large history can hold
    /// megabytes of tool output and building every node up front is what stopped the
    /// window from responding.
    public IReadOnlyList<JsonTreeNode> TreeNodes
    {
        get
        {
            if (_treeNodes is not null)
            {
                return _treeNodes;
            }

            if (!_isExpanded || _treeSource is null)
            {
                return [];
            }

            _treeNodes = JsonTreeNode.Parse(_treeSource);
            return _treeNodes;
        }
    }

    /// Pretty-printed payload, monospaced, exact and copyable.
    public IReadOnlyList<MessageBlock> CodeBlocks { get; }

    public bool IsCodeMode => _isCodeMode;
    public bool ShowTree => HasTree && !IsCodeMode;
    public double BodyMaxHeight => Text.Length > 1200 ? 360 : 2000;

    public string ModeToggleText => IsCodeMode ? "🗂 Navigable" : "{ } Code";

    public bool IsExpanded
    {
        get => _isExpanded;
        set => this.RaiseAndSetIfChanged(ref _isExpanded, value);
    }

    public string ExpandButtonText => IsExpanded ? "▲ Hide Output" : "▼ Show Output";

    public void ToggleExpand()
    {
        IsExpanded = !IsExpanded;

        if (IsExpanded && _treeNodes is null && _treeSource is not null)
        {
            // Materialise the tree now that there is something to show.
            _treeNodes = JsonTreeNode.Parse(_treeSource);
            this.RaisePropertyChanged(nameof(TreeNodes));
            this.RaisePropertyChanged(nameof(ShowTree));
        }

        this.RaisePropertyChanged(nameof(ExpandButtonText));
    }

    public void ToggleMode()
    {
        _isCodeMode = !_isCodeMode;
        this.RaisePropertyChanged(nameof(IsCodeMode));
        this.RaisePropertyChanged(nameof(ShowTree));
        this.RaisePropertyChanged(nameof(ModeToggleText));
    }

    public ChatMessageRow(
        string role,
        string text,
        string? toolName,
        string? arguments = null,
        bool startsExpanded = true)
    {
        Role = role;
        Text = text;
        ToolName = toolName;
        Arguments = arguments;
        _isExpanded = startsExpanded;

        CodeBlocks = IsAssistant
            ? SimpleMarkdownParser.Parse(text)
            : ToolOutputFormatter.BuildRawBlocks(text);

        _treeSource = IsTool && !ToolOutputFormatter.IsError(text) ? text : null;

        if (startsExpanded)
        {
            _treeNodes = _treeSource is null ? [] : JsonTreeNode.Parse(_treeSource);
        }
    }
}
