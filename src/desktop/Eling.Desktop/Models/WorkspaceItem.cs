using System;
using System.IO;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace Eling.Desktop.Models;

/// <summary>
/// One selectable row in the memory sidebar's workspace picker. Like the
/// dashboard's <c>WorkspaceRow</c>, identity is the workspace root returned by
/// <c>/api/coordinator/runtimes?includeRegistered=true</c>.
/// <para>
/// This is a plain view-model item rather than <see cref="RuntimeDto"/> because
/// the picker needs a per-row <see cref="IsSelected"/> the user can toggle
/// (checkbox), which a positional record cannot carry.
/// </para>
/// </summary>
public sealed class WorkspaceItem : ReactiveObject
{
    private bool _isSelected;

    public WorkspaceItem(string root, string dataDirectory)
    {
        Root = root;
        DataDirectory = dataDirectory;
    }

    public string Root { get; }

    public string DataDirectory { get; }

    public string Name => Path.GetFileName(Root.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : Root;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            this.RaisePropertyChanged(nameof(IsSelected));
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Raised after <see cref="IsSelected"/> changes, so the owner can
    /// re-query without subscribing to <see cref="ReactiveObject.PropertyChanged"/>.</summary>
    public event EventHandler? SelectionChanged;
}
