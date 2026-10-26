using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Eling.Desktop.Models;
using Eling.Desktop.Services;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace Eling.Desktop.ViewModels;

public class MainViewModel : ViewModelBase, IDisposable
{
    private readonly BackendSupervisor _backendSupervisor;
    private readonly ElingApiClient _apiClient;
    private readonly ILogger<MainViewModel> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly DesktopSettingsStore? _settingsStore;
    private readonly Dictionary<string, bool> _workspaceExpansion = new(StringComparer.Ordinal);
    private List<MemoryWorkspaceNode> _workspaceRoots = [];
    private WorkspaceTreesDto? _trees;
    private SseClient? _sseClient;
    private CancellationTokenSource? _cts;

    private string _statusText = "Starting...";
    private bool _isLoading;
    private bool _isConnected;
    private string _searchQuery = "";
    private string _selectedType = "All";
    private bool _allSelected = true;
    private string _tier = "all";
    private bool _globalIncluded = false;
    private string _sseStatus = "connecting";
    private string _errorMessage = "";
    private bool _hasLoadedOnce = false;
    private int _totalInScope = 0;
    private int _matchingCount = 0;
    private Avalonia.Threading.DispatcherTimer? _searchDebounceTimer;
    private Avalonia.Threading.DispatcherTimer? _errorDismissTimer;

    // Edit states
    private MemoryDto? _editingMemory;
    private string _editContent = "";
    private string _editType = "Note";
    private string _editStatus = "Active";
    private string _editTags = "";

    // Dialog states
    private MemoryDto? _deleteTarget;
    private MemoryDto? _promoteTarget;
    private bool _promoteAsMove;
    private MemoryDto? _copyTarget;
    private bool _copyAsMove;
    private string _copyProjectRoot = "";
    private WorkspaceItem? _copyTargetWorkspace;
    private bool _suppressWorkspaceReload;
    private bool _showCreateDialog;
    private string _createContent = "";
    private string _createType = "Note";
    private string _createTags = "";
    private string _createScope = "Current Project";

    public string StatusText { get => _statusText; set => this.RaiseAndSetIfChanged(ref _statusText, value); }
    public event Action? OnWorkspaceRefreshRequested;

    private void NotifyWorkspaceSseRefresh()
    {
        OnWorkspaceRefreshRequested?.Invoke();
    }
    public bool IsLoading { get => _isLoading; set => this.RaiseAndSetIfChanged(ref _isLoading, value); }
    public bool IsConnected { get => _isConnected; set => this.RaiseAndSetIfChanged(ref _isConnected, value); }
    public string SseStatus { get => _sseStatus; set => this.RaiseAndSetIfChanged(ref _sseStatus, value); }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            this.RaiseAndSetIfChanged(ref _searchQuery, value);
            ScheduleSearchDebounce();
        }
    }

    private void ScheduleSearchDebounce()
    {
        if (_searchDebounceTimer == null)
        {
            _searchDebounceTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _searchDebounceTimer.Tick += (_, _) =>
            {
                _searchDebounceTimer?.Stop();
                _ = LoadMemoriesAsync();
            };
        }
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    public string SelectedType
    {
        get => _selectedType;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedType, value);
            _ = LoadMemoriesAsync();
        }
    }

    public string Tier
    {
        get => _tier;
        set
        {
            this.RaiseAndSetIfChanged(ref _tier, value);
            if (TierApplies) _ = LoadMemoriesAsync();
        }
    }

    public bool GlobalIncluded
    {
        get => _globalIncluded;
        set
        {
            this.RaiseAndSetIfChanged(ref _globalIncluded, value);
            this.RaisePropertyChanged(nameof(GlobalToggleText));
            this.RaisePropertyChanged(nameof(ScopeDisplay));
            _ = LoadMemoriesAsync();
        }
    }

    public string ErrorMessage { get => _errorMessage; set => this.RaiseAndSetIfChanged(ref _errorMessage, value); }
    public bool HasLoadedOnce { get => _hasLoadedOnce; set => this.RaiseAndSetIfChanged(ref _hasLoadedOnce, value); }
    public int TotalInScope { get => _totalInScope; set => this.RaiseAndSetIfChanged(ref _totalInScope, value); }
    public int MatchingCount { get => _matchingCount; set => this.RaiseAndSetIfChanged(ref _matchingCount, value); }

    public bool AllSelected
    {
        get => _allSelected;
        set
        {
            if (_allSelected == value) return;
            _logger.LogInformation("All workspaces switched to {Value} ({Count} workspaces)", value, Workspaces.Count);
            ApplyWorkspaceSelection(
                () => MemoryWorkspaceSelection.SetAll(Workspaces, value),
                value);
        }
    }

    public string AllWorkspacesBackground => AllSelected ? "#27272a" : "Transparent";

    public bool TierApplies => !AllSelected && SelectedWorkspaceCount == 1;

    // Edit properties
    public MemoryDto? EditingMemory { get => _editingMemory; set => this.RaiseAndSetIfChanged(ref _editingMemory, value); }
    public string EditContent { get => _editContent; set => this.RaiseAndSetIfChanged(ref _editContent, value); }
    public string EditType { get => _editType; set => this.RaiseAndSetIfChanged(ref _editType, value); }
    public string EditStatus { get => _editStatus; set => this.RaiseAndSetIfChanged(ref _editStatus, value); }
    public string EditTags { get => _editTags; set => this.RaiseAndSetIfChanged(ref _editTags, value); }

    // Dialog properties
    public MemoryDto? DeleteTarget { get => _deleteTarget; set => this.RaiseAndSetIfChanged(ref _deleteTarget, value); }
    public MemoryDto? PromoteTarget { get => _promoteTarget; set => this.RaiseAndSetIfChanged(ref _promoteTarget, value); }
    public bool PromoteAsMove { get => _promoteAsMove; set => this.RaiseAndSetIfChanged(ref _promoteAsMove, value); }
    public MemoryDto? CopyTarget { get => _copyTarget; set => this.RaiseAndSetIfChanged(ref _copyTarget, value); }
    public bool CopyAsMove { get => _copyAsMove; set => this.RaiseAndSetIfChanged(ref _copyAsMove, value); }
    public string CopyProjectRoot { get => _copyProjectRoot; set => this.RaiseAndSetIfChanged(ref _copyProjectRoot, value); }
    public WorkspaceItem? CopyTargetWorkspace
    {
        get => _copyTargetWorkspace;
        set
        {
            this.RaiseAndSetIfChanged(ref _copyTargetWorkspace, value);
            CopyProjectRoot = value?.Root ?? "";
        }
    }    public bool ShowCreateDialog { get => _showCreateDialog; set => this.RaiseAndSetIfChanged(ref _showCreateDialog, value); }
    public string CreateContent { get => _createContent; set => this.RaiseAndSetIfChanged(ref _createContent, value); }
    public string CreateType { get => _createType; set => this.RaiseAndSetIfChanged(ref _createType, value); }
    public string CreateTags { get => _createTags; set => this.RaiseAndSetIfChanged(ref _createTags, value); }
    public string CreateScope { get => _createScope; set => this.RaiseAndSetIfChanged(ref _createScope, value); }

    public int TotalMemoriesCount => TotalInScope;
    public int FilteredMemoriesCount => MatchingCount;
    public string MemoryCountStatus => $"Showing {MatchingCount} of {TotalInScope} memories";
    public string ScopeDisplay => AllSelected
        ? "All workspaces"
        : (SelectedWorkspaceCount == 0
            ? "Global"
            : string.Join(", ", SelectedWorkspaces.Select(w => w.Name)));
    public bool IsEmpty => HasLoadedOnce && !IsLoading && FilteredMemories.Count == 0;
    public string GlobalToggleText => GlobalIncluded ? "✓ Global" : "🌐 Global";
    public string ConnectionDetails => IsConnected ? $"{_backendSupervisor.BaseUrl} (Port: {_backendSupervisor.ActivePort})" : "Disconnected";

    public ObservableCollection<MemoryDto> Memories { get; } = [];
    public ObservableCollection<MemoryDto> FilteredMemories { get; } = [];
    /// <summary>Registered workspaces (live + remembered) shown in the memory sidebar,
    /// mirroring the dashboard's <c>/api/coordinator/runtimes?includeRegistered=true</c> rows.</summary>
    public ObservableCollection<WorkspaceItem> Workspaces { get; } = [];
    public ObservableCollection<MemoryWorkspaceNode> VisibleWorkspaces { get; } = [];
    public bool HasNoWorkspaces => Workspaces.Count == 0;

    public ReactiveCommand<string, System.Reactive.Unit> SetWorkspaceGroupModeCommand { get; }
    public ReactiveCommand<MemoryWorkspaceNode, System.Reactive.Unit> ToggleWorkspaceGroupCommand { get; }
    public ReactiveCommand<MemoryWorkspaceNode, System.Reactive.Unit> ToggleWorkspaceExpansionCommand { get; }

    private int _workspaceGroupMode = (int)WorkspaceGrouping.Mode.Repo;
    public int WorkspaceGroupMode
    {
        get => _workspaceGroupMode;
        set
        {
            if (value < (int)WorkspaceGrouping.Mode.Flat || value > (int)WorkspaceGrouping.Mode.Folder) return;
            if (_workspaceGroupMode == value) return;
            this.RaiseAndSetIfChanged(ref _workspaceGroupMode, value);
            _settingsStore?.SetWorkspaceGroupMode((WorkspaceGrouping.Mode)value);
            RebuildWorkspaceNodes();
        }
    }

    /// <summary>Workspaces the user ticked in the sidebar. Empty means "Global only"
    /// while <see cref="AllSelected"/> covers the aggregate view.</summary>
    public IReadOnlyList<WorkspaceItem> SelectedWorkspaces => Workspaces.Where(w => w.IsSelected).ToList();

    public int SelectedWorkspaceCount => Workspaces.Count(w => w.IsSelected);

    public string[] TypeOptions { get; } = ["Fact", "Preference", "Decision", "Lesson", "Note"];
    public string[] StatusOptions { get; } = ["Active", "Superseded", "Archived"];
    public string[] ScopeOptions { get; } = ["Current Project", "Global Scope"];

    // Commands
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> RefreshCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ClearSearchCommand { get; }
    public ReactiveCommand<string, System.Reactive.Unit> SelectTypeCommand { get; }
    public ReactiveCommand<string, System.Reactive.Unit> TierCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ToggleGlobalCommand { get; }
    public ReactiveCommand<string, System.Reactive.Unit> ToggleRootCommand { get; }
    public ReactiveCommand<WorkspaceItem, System.Reactive.Unit> SelectOnlyWorkspaceCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> SelectAllCommand { get; }

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> CreateMemoryCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> SaveCreateCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> CancelCreateCommand { get; }

    public ReactiveCommand<MemoryDto, System.Reactive.Unit> StartEditCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> SaveEditCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> CancelEditCommand { get; }

    public ReactiveCommand<MemoryDto, System.Reactive.Unit> PromptDeleteCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ConfirmDeleteCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> CancelDeleteCommand { get; }

    public ReactiveCommand<MemoryDto, System.Reactive.Unit> PromptPromoteCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ConfirmPromoteCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> CancelPromoteCommand { get; }

    public ReactiveCommand<MemoryDto, System.Reactive.Unit> PromptCopyCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ConfirmCopyCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> CancelCopyCommand { get; }

    public ReactiveCommand<MemoryDto, System.Reactive.Unit> MoveTierCommand { get; }

    public MainViewModel(
        BackendSupervisor backendSupervisor,
        ElingApiClient apiClient,
        ILogger<MainViewModel> logger,
        ILoggerFactory loggerFactory,
        DesktopSettingsStore? settingsStore = null)
    {
        _backendSupervisor = backendSupervisor;
        _apiClient = apiClient;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _settingsStore = settingsStore;
        _workspaceGroupMode = (int)(_settingsStore?.GetWorkspaceGroupMode() ?? WorkspaceGrouping.Mode.Repo);

        RefreshCommand = ReactiveCommand.CreateFromTask(() => LoadMemoriesAsync());
        ClearSearchCommand = ReactiveCommand.Create(() => { SearchQuery = ""; });
        SelectTypeCommand = ReactiveCommand.Create<string>(type => { SelectedType = type ?? "All"; });
        TierCommand = ReactiveCommand.Create<string>(tier => { Tier = tier ?? "all"; });
        ToggleGlobalCommand = ReactiveCommand.Create(() => { GlobalIncluded = !GlobalIncluded; });
        ToggleRootCommand = ReactiveCommand.Create<string>(root =>
        {
            if (string.IsNullOrWhiteSpace(root)) return;
            var item = Workspaces.FirstOrDefault(w => string.Equals(w.Root, root, StringComparison.OrdinalIgnoreCase));
            if (item is null) return;
            item.IsSelected = !item.IsSelected;
        });
        SelectAllCommand = ReactiveCommand.Create(() => { AllSelected = true; });
        SetWorkspaceGroupModeCommand = ReactiveCommand.Create<string>(mode =>
        {
            if (int.TryParse(mode, out var parsed)) WorkspaceGroupMode = parsed;
        });
        ToggleWorkspaceGroupCommand = ReactiveCommand.Create<MemoryWorkspaceNode>(node =>
        {
            if (node is null) return;
            ApplyWorkspaceSelection(() => MemoryWorkspaceSelection.ToggleGroup(Workspaces, node.Group));
        });
        ToggleWorkspaceExpansionCommand = ReactiveCommand.Create<MemoryWorkspaceNode>(node =>
        {
            if (node is null) return;
            node.IsExpanded = !node.IsExpanded;
            _workspaceExpansion[node.Id] = node.IsExpanded;
            RebuildVisibleWorkspaces();
        });

        // The name selects one workspace; its checkbox toggles the whole node group.
        SelectOnlyWorkspaceCommand = ReactiveCommand.Create<WorkspaceItem>(item =>
        {
            if (item is null) return;
            ApplyWorkspaceSelection(
                () => MemoryWorkspaceSelection.SelectOnly(Workspaces, item),
                false);
        });

        CreateMemoryCommand = ReactiveCommand.Create(() => { ShowCreateDialog = true; });
        SaveCreateCommand = ReactiveCommand.CreateFromTask(() => CreateMemoryAsync());
        CancelCreateCommand = ReactiveCommand.Create(() => { ShowCreateDialog = false; });

        StartEditCommand = ReactiveCommand.Create<MemoryDto>(m =>
        {
            if (m == null) return;
            StartEdit(m);
        });
        SaveEditCommand = ReactiveCommand.CreateFromTask(() => SaveEditAsync());
        CancelEditCommand = ReactiveCommand.Create(() => { EditingMemory = null; EditTags = ""; });

        PromptDeleteCommand = ReactiveCommand.Create<MemoryDto>(m => { DeleteTarget = m; });
        ConfirmDeleteCommand = ReactiveCommand.CreateFromTask(() => DeleteMemoryAsync());
        CancelDeleteCommand = ReactiveCommand.Create(() => { DeleteTarget = null; });

        PromptPromoteCommand = ReactiveCommand.Create<MemoryDto>(m =>
        {
            if (m == null) return;
            PromoteTarget = m;
            PromoteAsMove = false;
        });
        ConfirmPromoteCommand = ReactiveCommand.CreateFromTask(() => PromoteToGlobalAsync());
        CancelPromoteCommand = ReactiveCommand.Create(() => { PromoteTarget = null; });

        PromptCopyCommand = ReactiveCommand.Create<MemoryDto>(m =>
        {
            if (m == null) return;
            CopyTarget = m;
            CopyAsMove = false;
            CopyTargetWorkspace = Workspaces.FirstOrDefault();
        });
        ConfirmCopyCommand = ReactiveCommand.CreateFromTask(() => CopyToProjectAsync());
        CancelCopyCommand = ReactiveCommand.Create(() => { CopyTarget = null; });

        MoveTierCommand = ReactiveCommand.CreateFromTask<MemoryDto>(async m =>
        {
            if (m == null) return;
            var target = m.IsLocalScope ? "project" : "project-local";
            var ok = await _apiClient.MoveMemoryTierAsync(
                m.Id, m.Scope ?? "all", m.Project?.Root ?? "", target);
            if (ok) await LoadMemoriesAsync();
        });
    }

    public async Task InitializeAsync()
    {
        try
        {
            _logger.LogInformation("Initializing desktop UI...");
            StatusText = "Connecting to backend...";
            var baseUrl = await _backendSupervisor.EnsureBackendRunningAsync();
            IsConnected = true;
            StatusText = $"Connected to {baseUrl}";
            _logger.LogInformation("Connected to backend at {Url}", baseUrl);

            _sseClient = new SseClient(baseUrl, _loggerFactory.CreateLogger<SseClient>());
            _sseClient.OnMemoryChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = LoadMemoriesAsync());
            _sseClient.OnRuntimesChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _ = LoadWorkspacesAsync();
                NotifyWorkspaceSseRefresh();
            });
            _sseClient.OnCodebaseChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                NotifyWorkspaceSseRefresh();
            });
            _sseClient.OnStatusChanged += status => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                SseStatus = status;
                this.RaisePropertyChanged(nameof(ConnectionDetails));
            });
            _sseClient.Start();

            await LoadWorkspacesAsync();
            await LoadMemoriesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize");
            StatusText = $"Backend error: {ex.Message}";
            IsConnected = false;
        }
    }

    public async Task LoadMemoriesAsync()
    {
        IsLoading = true;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        try
        {
            _logger.LogInformation("Loading memories (all={All}, roots={Roots}, global={Global}, type={Type}, tier={Tier})...", AllSelected, string.Join(",", SelectedWorkspaces.Select(w => w.Root)), GlobalIncluded, SelectedType, Tier);
            var roots = AllSelected ? null : SelectedWorkspaces.Select(w => w.Root).ToList();
            var result = await _apiClient.GetMemoriesAsync(
                AllSelected,
                roots,
                GlobalIncluded,
                limit: 200,
                type: SelectedType,
                q: SearchQuery,
                tier: TierApplies ? Tier : null,
                _cts.Token);

            Memories.Clear();
            FilteredMemories.Clear();
            if (result != null)
            {
                foreach (var memory in result.Items)
                {
                    Memories.Add(memory);
                    FilteredMemories.Add(memory);
                }
                TotalInScope = result.Metadata.Total;
                MatchingCount = result.Metadata.Matching;
            }
            else
            {
                TotalInScope = 0;
                MatchingCount = 0;
            }

            HasLoadedOnce = true;
            ErrorMessage = "";
            this.RaisePropertyChanged(nameof(IsEmpty));
            StatusText = IsConnected ? $"Connected | {MatchingCount} of {TotalInScope} memories" : StatusText;
            _logger.LogInformation("Loaded {Count} memories", FilteredMemories.Count);

            this.RaisePropertyChanged(nameof(TotalMemoriesCount));
            this.RaisePropertyChanged(nameof(FilteredMemoriesCount));
            this.RaisePropertyChanged(nameof(MemoryCountStatus));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load memories");
            ErrorMessage = ex.Message;
            ScheduleErrorDismiss();
            this.RaisePropertyChanged(nameof(IsEmpty));
            StatusText = $"Load failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
            this.RaisePropertyChanged(nameof(IsEmpty));
        }
    }

    private void ScheduleErrorDismiss()
    {
        if (_errorDismissTimer == null)
        {
            _errorDismissTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _errorDismissTimer.Tick += (_, _) =>
            {
                _errorDismissTimer?.Stop();
                ErrorMessage = "";
            };
        }
        _errorDismissTimer.Stop();
        _errorDismissTimer.Start();
    }

    /// <summary>
    /// Loads the workspace list for the memory sidebar. Uses registered runtimes
    /// (live + remembered) so scopes stay listed after their process exits, and
    /// shapes them exactly like the dashboard: workspace root, skipping
    /// codebase-disabled runtimes and the "UserScope" sentinel, deduped
    /// case-insensitively.
    /// </summary>
    private async Task LoadWorkspacesAsync()
    {
        try
        {
            var runtimes = await _apiClient.GetAllCoordinatorRuntimesAsync();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rows = new List<WorkspaceItem>();
            foreach (var runtime in runtimes)
            {
                if (!runtime.CodebaseEnabled) continue;
                var root = !string.IsNullOrWhiteSpace(runtime.WorkspaceRoot)
                    ? runtime.WorkspaceRoot
                    : runtime.HeadScopeRoot;
                if (string.IsNullOrWhiteSpace(root)) continue;
                if (string.Equals(root, "UserScope", StringComparison.OrdinalIgnoreCase)) continue;
                if (!seen.Add(root)) continue;
                rows.Add(new WorkspaceItem(root, runtime.DataDirectory, runtime.RepositoryRoot)
                {
                    IsAlive = runtime.IsAlive
                });
            }

            // Retain object identity so ticks survive refreshes, but refresh runtime metadata.
            var keep = rows.Select(row => row.Root).ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (var i = Workspaces.Count - 1; i >= 0; i--)
            {
                if (!keep.Contains(Workspaces[i].Root)) Workspaces.RemoveAt(i);
            }

            var present = Workspaces.ToDictionary(workspace => workspace.Root, StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                if (present.TryGetValue(row.Root, out var existing))
                {
                    existing.UpdateRuntime(row.DataDirectory, row.RepositoryRoot, row.IsAlive);
                    continue;
                }

                row.IsSelected = _allSelected;
                row.SelectionChanged += OnWorkspaceSelectionChanged;
                Workspaces.Add(row);
                present.Add(row.Root, row);
            }

            var trees = await _apiClient.GetWorkspaceTreesAsync();
            if (trees is not null) _trees = trees;

            RebuildWorkspaceNodes();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load workspaces");
        }
    }

    private WorkspaceItem? LookupWorkspace(string root)
    {
        return Workspaces.FirstOrDefault(w => string.Equals(w.Root, root, StringComparison.OrdinalIgnoreCase));
    }

    private void RebuildWorkspaceNodes()
    {
        if (_trees is not null)
        {
            var nodes = WorkspaceGroupMode switch
            {
                (int)Eling.Core.Workspaces.WorkspaceGroupMode.Flat => _trees.Flat,
                (int)Eling.Core.Workspaces.WorkspaceGroupMode.Folder => _trees.Folder,
                _ => _trees.Repo
            };

            MemoryWorkspaceTree.CaptureExpansion(_workspaceRoots, _workspaceExpansion);
            _workspaceRoots = WorkspaceTreeMapper.ToMemoryNodes(nodes, LookupWorkspace);
            MemoryWorkspaceTree.RestoreExpansion(_workspaceRoots, _workspaceExpansion);
            MemoryWorkspaceTree.RefreshAggregates(_workspaceRoots);
        }
        else
        {
            MemoryWorkspaceTree.CaptureExpansion(_workspaceRoots, _workspaceExpansion);
            _workspaceRoots = MemoryWorkspaceTree.Build(
                Workspaces.ToList(),
                (WorkspaceGrouping.Mode)WorkspaceGroupMode);
            MemoryWorkspaceTree.RestoreExpansion(_workspaceRoots, _workspaceExpansion);
            MemoryWorkspaceTree.RefreshAggregates(_workspaceRoots);
        }
        RebuildVisibleWorkspaces();
        this.RaisePropertyChanged(nameof(HasNoWorkspaces));
    }

    private void RebuildVisibleWorkspaces()
    {
        VisibleWorkspaces.Clear();
        foreach (var node in MemoryWorkspaceTree.FlattenVisible(_workspaceRoots))
            VisibleWorkspaces.Add(node);
    }

    private void ApplyWorkspaceSelection(Action change, bool? allSelected = null)
    {
        _suppressWorkspaceReload = true;
        try
        {
            change();
        }
        finally
        {
            _suppressWorkspaceReload = false;
        }
        NotifyWorkspaceSelectionChanged(allSelected);
    }

    private void OnWorkspaceSelectionChanged(object? sender, EventArgs e)
    {
        if (!_suppressWorkspaceReload) NotifyWorkspaceSelectionChanged();
    }

    private void NotifyWorkspaceSelectionChanged(bool? allSelected = null)
    {
        var nextAll = allSelected ?? (Workspaces.Count > 0 && Workspaces.All(workspace => workspace.IsSelected));
        if (_allSelected != nextAll)
        {
            _allSelected = nextAll;
            this.RaisePropertyChanged(nameof(AllSelected));
        }

        this.RaisePropertyChanged(nameof(AllWorkspacesBackground));
        this.RaisePropertyChanged(nameof(TierApplies));
        this.RaisePropertyChanged(nameof(ScopeDisplay));
        this.RaisePropertyChanged(nameof(SelectedWorkspaceCount));
        MemoryWorkspaceTree.RefreshAggregates(_workspaceRoots);
        _ = LoadMemoriesAsync();
    }

    private async Task CreateMemoryAsync()
    {
        if (string.IsNullOrWhiteSpace(CreateContent)) return;
        try
        {
            var tags = CreateTags.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).ToList();
            var apiScope = CreateScope == "Global Scope" ? "global" : "all";
            var result = await _apiClient.CreateMemoryAsync(CreateContent.Trim(), CreateType, tags, apiScope);
            if (result != null)
            {
                await LoadMemoriesAsync();
                ShowCreateDialog = false;
                CreateContent = "";
                CreateType = "Note";
                CreateTags = "";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create memory");
        }
    }

    private async Task SaveEditAsync()
    {
        if (EditingMemory == null) return;
        try
        {
            var tags = EditTags.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).ToList();
            var result = await _apiClient.UpdateMemoryAsync(
                EditingMemory.Id, EditContent, EditType, EditStatus,
                EditingMemory.Scope ?? "all",
                EditingMemory.Project?.Root,
                EditingMemory.IsLocalScope ? "project-local" : null,
                tags);

            if (result != null)
            {
                await LoadMemoriesAsync();
                EditingMemory = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update memory");
        }
    }

    private async Task DeleteMemoryAsync()
    {
        if (DeleteTarget == null) return;
        try
        {
            if (await _apiClient.DeleteMemoryAsync(DeleteTarget.Id, DeleteTarget.Scope ?? "all", DeleteTarget.Project?.Root, DeleteTarget.IsLocalScope ? "project-local" : null))
            {
                await LoadMemoriesAsync();
                DeleteTarget = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete memory");
        }
    }

    private async Task PromoteToGlobalAsync()
    {
        if (PromoteTarget == null) return;
        try
        {
            var projectRoot = PromoteTarget.Project?.Root ?? "";
            if (await _apiClient.PromoteToGlobalAsync(PromoteTarget.Id, projectRoot, PromoteAsMove))
            {
                await LoadMemoriesAsync();
                PromoteTarget = null;
                PromoteAsMove = false;
                StatusText = "Memory promoted to global";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to promote memory");
            StatusText = $"Promote failed: {ex.Message}";
        }
    }

    private async Task CopyToProjectAsync()
    {
        if (CopyTarget == null || string.IsNullOrEmpty(CopyProjectRoot)) return;
        try
        {
            if (await _apiClient.CopyToProjectAsync(CopyTarget.Id, CopyTarget.Scope ?? "global", CopyTarget.Project?.Root, CopyProjectRoot, CopyAsMove))
            {
                await LoadMemoriesAsync();
                CopyTarget = null;
                CopyAsMove = false;
                CopyProjectRoot = "";
                StatusText = "Memory copied to project";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to copy memory");
            StatusText = $"Copy failed: {ex.Message}";
        }
    }

    public void StartEdit(MemoryDto memory)
    {
        EditingMemory = memory;
        EditContent = memory.Content;
        EditType = memory.Type;
        EditStatus = memory.Status;
        EditTags = string.Join(",", memory.Tags ?? new List<string>());
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException ex) { _logger.LogDebug(ex, "Cancellation source already disposed during shutdown"); }
        try { _cts?.Dispose(); } catch (ObjectDisposedException ex) { _logger.LogDebug(ex, "Cancellation source already disposed during shutdown"); }
        _sseClient?.Dispose();
        _backendSupervisor.Dispose();
    }
}
