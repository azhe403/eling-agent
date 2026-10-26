using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CliWrap;
using Eling.Core.Workspaces;
using Eling.Desktop.Models;
using Eling.Desktop.Services;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace Eling.Desktop.ViewModels;

public class FoldersViewModel : ViewModelBase
{
    private readonly ElingApiClient _apiClient;
    private readonly ILogger<FoldersViewModel> _logger;
    private readonly MainViewModel? _mainViewModel;

    private string _selectedRoot = "";
    private string _statusText = "";
    private string _newRootPath = "";
    private bool _showBrowser;

    // Rich workspace view fields matching the web dashboard
    private string _searchQuery = "";
    private string _filterStatus = "all"; // "all" | "active" | "offline"
    private WorkspaceGroupMode _groupMode = WorkspaceGroupMode.Repo;
    private WorkspaceTreesDto? _trees;
    private CancellationTokenSource? _filterCts;
    private bool _isLoading;
    private int _registeredCount;
    private int _activeInstancesCount;
    private int _activeWorkspacesCount;
    private int _totalFilesCount;
    private long _totalDbSizeBytes;
    private string _totalDbSizeDisplay = "0 B";
    private WorkspaceItemDto? _deleteTarget;

    // Delete scope flags matching dashboard delete modal
    private bool _deleteRegistry = true;
    private bool _deleteCodebaseIndex = true;
    private bool _deleteDotEling = false;

    public string SelectedRoot { get => _selectedRoot; set => this.RaiseAndSetIfChanged(ref _selectedRoot, value); }
    public string StatusText { get => _statusText; set => this.RaiseAndSetIfChanged(ref _statusText, value); }
    public string NewRootPath { get => _newRootPath; set => this.RaiseAndSetIfChanged(ref _newRootPath, value); }
    public bool ShowBrowser { get => _showBrowser; set => this.RaiseAndSetIfChanged(ref _showBrowser, value); }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            this.RaiseAndSetIfChanged(ref _searchQuery, value);
            _ = ApplyFilterAsync();
        }
    }

    public string FilterStatus
    {
        get => _filterStatus;
        set
        {
            this.RaiseAndSetIfChanged(ref _filterStatus, value);
            _ = ApplyFilterAsync();
        }
    }

    public bool IsLoading { get => _isLoading; set => this.RaiseAndSetIfChanged(ref _isLoading, value); }
    public int RegisteredCount { get => _registeredCount; set => this.RaiseAndSetIfChanged(ref _registeredCount, value); }
    public int ActiveInstancesCount { get => _activeInstancesCount; set => this.RaiseAndSetIfChanged(ref _activeInstancesCount, value); }
    public int ActiveWorkspacesCount { get => _activeWorkspacesCount; set => this.RaiseAndSetIfChanged(ref _activeWorkspacesCount, value); }
    public int TotalFilesCount { get => _totalFilesCount; set => this.RaiseAndSetIfChanged(ref _totalFilesCount, value); }
    public long TotalDbSizeBytes { get => _totalDbSizeBytes; set => this.RaiseAndSetIfChanged(ref _totalDbSizeBytes, value); }
    public string TotalDbSizeDisplay { get => _totalDbSizeDisplay; set => this.RaiseAndSetIfChanged(ref _totalDbSizeDisplay, value); }
    public WorkspaceItemDto? DeleteTarget { get => _deleteTarget; set => this.RaiseAndSetIfChanged(ref _deleteTarget, value); }

    public bool DeleteRegistry { get => _deleteRegistry; set => this.RaiseAndSetIfChanged(ref _deleteRegistry, value); }
    public bool DeleteCodebaseIndex { get => _deleteCodebaseIndex; set => this.RaiseAndSetIfChanged(ref _deleteCodebaseIndex, value); }
    public bool DeleteDotEling { get => _deleteDotEling; set => this.RaiseAndSetIfChanged(ref _deleteDotEling, value); }

    // Keep Roots collection for regression test suites and chat picker interop
    public ObservableCollection<string> Roots { get; } = [];

    // Rich workspaces list matching dashboard
    private readonly List<WorkspaceItemDto> _allWorkspaces = [];
    public ObservableCollection<WorkspaceItemDto> Workspaces { get; } = [];
    public ObservableCollection<WorkspaceNode> GroupedNodes { get; } = [];

    public WorkspaceGroupMode GroupMode
    {
        get => _groupMode;
        set
        {
            this.RaiseAndSetIfChanged(ref _groupMode, value);
            RebuildGroupedNodes();
        }
    }

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> LoadCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> AddRootCommand { get; }
    public ReactiveCommand<string?, System.Reactive.Unit> RemoveRootCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> RefreshCommand { get; }
    public ReactiveCommand<string, System.Reactive.Unit> SwitchWorkspaceCommand { get; }

    public ReactiveCommand<string, System.Reactive.Unit> SetFilterCommand { get; }
    public ReactiveCommand<string, System.Reactive.Unit> SetGroupModeCommand { get; }
    public ReactiveCommand<WorkspaceItemDto, System.Reactive.Unit> ReindexCommand { get; }
    public ReactiveCommand<WorkspaceItemDto, System.Reactive.Unit> ViewCodebaseCommand { get; }
    public ReactiveCommand<WorkspaceItemDto, System.Reactive.Unit> ToggleProcessesCommand { get; }
    public ReactiveCommand<WorkspaceItemDto, System.Reactive.Unit> PromptDeleteCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ConfirmDeleteCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> CancelDeleteCommand { get; }

    public FoldersViewModel(
        ElingApiClient apiClient,
        ILogger<FoldersViewModel> logger,
        MainViewModel? mainViewModel = null)
    {
        _apiClient = apiClient;
        _logger = logger;
        _mainViewModel = mainViewModel;

        LoadCommand = ReactiveCommand.CreateFromTask(() => LoadAsync());
        AddRootCommand = ReactiveCommand.CreateFromTask(() => AddRootAsync());
        RemoveRootCommand = ReactiveCommand.CreateFromTask<string?>(target => RemoveRootAsync(target));
        RefreshCommand = ReactiveCommand.CreateFromTask(() => LoadAsync());
        SwitchWorkspaceCommand = ReactiveCommand.CreateFromTask<string>(root => SwitchWorkspaceAsync(root));

        this.WhenAnyValue(x => x.StatusText)
            .Subscribe(text =>
            {
                if (!string.IsNullOrEmpty(text) && _mainViewModel != null)
                {
                    _mainViewModel.StatusText = text;
                }
            });

        SetFilterCommand = ReactiveCommand.Create<string>(status => FilterStatus = status);
        SetGroupModeCommand = ReactiveCommand.Create<string>(mode =>
        {
            if (Enum.TryParse<WorkspaceGroupMode>(mode, true, out var m))
                GroupMode = m;
        });
        ReindexCommand = ReactiveCommand.CreateFromTask<WorkspaceItemDto>(ws => ReindexAsync(ws));
        ViewCodebaseCommand = ReactiveCommand.Create<WorkspaceItemDto>(ws => OpenCodebaseInBrowser(ws));
        ToggleProcessesCommand = ReactiveCommand.Create<WorkspaceItemDto>(ws => ws?.ToggleExpanded());
        PromptDeleteCommand = ReactiveCommand.Create<WorkspaceItemDto>(ws =>
        {
            DeleteTarget = ws;
            DeleteRegistry = true;
            DeleteCodebaseIndex = true;
            DeleteDotEling = false;
        });
        ConfirmDeleteCommand = ReactiveCommand.CreateFromTask(() => ConfirmDeleteAsync());
        CancelDeleteCommand = ReactiveCommand.Create(() => { DeleteTarget = null; });
    }

    public int ApiLoadRetryAttempts { get; set; } = BackendResilience.DefaultAttempts;
    public TimeSpan ApiLoadRetryDelay { get; set; } = BackendResilience.BaseDelay;
    public TimeSpan ApiLoadRetryMaxDelay { get; set; } = BackendResilience.MaxDelay;

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var pipeline = BackendResilience.ForResult<IReadOnlyList<string>?>(
                static roots => roots is null,
                _logger,
                ApiLoadRetryAttempts,
                ApiLoadRetryDelay,
                ApiLoadRetryMaxDelay,
                "folder-load");

            var agentRoots = await pipeline.ExecuteAsync<IReadOnlyList<string>?>(
                _ => new ValueTask<IReadOnlyList<string>?>(_apiClient.GetWorkspacesAsync()));

            var runtimesTask = _apiClient.GetAllCoordinatorRuntimesAsync();
            var statusesTask = _apiClient.GetProjectCodebaseStatusesAsync();

            await Task.WhenAll(runtimesTask, statusesTask);

            var runtimes = await runtimesTask;
            var statuses = await statusesTask;

            Roots.Clear();
            if (agentRoots != null)
            {
                foreach (var r in agentRoots) Roots.Add(r);
            }

            var agg = WorkspaceAggregator.Aggregate(runtimes, statuses, agentRoots);

            _allWorkspaces.Clear();
            _allWorkspaces.AddRange(agg.Workspaces);

            RegisteredCount = agg.RegisteredCount;
            ActiveInstancesCount = agg.ActiveInstancesCount;
            ActiveWorkspacesCount = agg.ActiveWorkspacesCount;
            TotalFilesCount = agg.TotalFilesCount;
            TotalDbSizeBytes = agg.TotalDbSizeBytes;
            TotalDbSizeDisplay = WorkspaceItemDto.FormatBytes(agg.TotalDbSizeBytes);

            await ApplyFilterAsync(debounce: false);

            if (!Roots.Contains(SelectedRoot))
            {
                SelectedRoot = Roots.FirstOrDefault() ?? "";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load workspaces");
            StatusText = $"Load failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task ApplyFilterAsync(bool debounce = true)
    {
        _filterCts?.Cancel();
        _filterCts?.Dispose();
        var cts = new CancellationTokenSource();
        _filterCts = cts;

        try
        {
            if (debounce)
            {
                await Task.Delay(150, cts.Token);
            }

            var status = FilterStatus == "all" ? null : FilterStatus;
            var q = string.IsNullOrWhiteSpace(_searchQuery) ? null : _searchQuery.Trim();

            var trees = await _apiClient.GetWorkspaceTreesAsync(status, q, cts.Token);
            if (cts.IsCancellationRequested || trees is null) return;

            _trees = trees;

            var byRoot = _allWorkspaces.ToDictionary(ws => ws.Root, StringComparer.OrdinalIgnoreCase);
            var filteredWorkspaces = new List<WorkspaceItemDto>();
            foreach (var row in trees.Rows)
            {
                if (byRoot.TryGetValue(row.Root, out var existing))
                {
                    filteredWorkspaces.Add(existing);
                }
                else
                {
                    filteredWorkspaces.Add(new WorkspaceItemDto(
                        root: row.Root,
                        repositoryRoot: row.RepositoryRoot,
                        name: row.Name,
                        isRunning: row.Alive,
                        processCount: row.ProcessCount,
                        singlePid: null,
                        transport: null,
                        ownerPort: null,
                        lastHeartbeat: DateTimeOffset.UtcNow,
                        totalMemoryBytes: null,
                        totalCpuPercent: null,
                        files: 0,
                        chunks: 0,
                        lastIndexedAt: null,
                        dbFileName: null,
                        dbPath: null,
                        dbSizeBytes: 0,
                        processes: []));
                }
            }

            Workspaces.Clear();
            foreach (var ws in filteredWorkspaces) Workspaces.Add(ws);

            RebuildGroupedNodes();
        }
        catch (OperationCanceledException)
        {
            // Suppressed on cancellation
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to apply workspace filter");
        }
    }

    private WorkspaceItemDto? LookupWorkspace(string root)
    {
        return _allWorkspaces.FirstOrDefault(ws => string.Equals(ws.Root, root, StringComparison.OrdinalIgnoreCase))
            ?? Workspaces.FirstOrDefault(ws => string.Equals(ws.Root, root, StringComparison.OrdinalIgnoreCase));
    }

    private void RebuildGroupedNodes()
    {
        GroupedNodes.Clear();
        if (_trees is null) return;

        var nodes = GroupMode switch
        {
            WorkspaceGroupMode.Flat => _trees.Flat,
            WorkspaceGroupMode.Folder => _trees.Folder,
            _ => _trees.Repo
        };

        foreach (var node in WorkspaceTreeMapper.ToFolderNodes(nodes, LookupWorkspace))
        {
            GroupedNodes.Add(node);
        }
    }

    private async Task ReindexAsync(WorkspaceItemDto? ws)
    {
        if (ws == null) return;
        StatusText = $"Re-indexing {ws.Name}...";
        try
        {
            var ok = await _apiClient.ReindexCodebaseAsync(ws.Root);
            StatusText = ok ? $"Re-index started for {ws.Name}" : $"Re-index failed for {ws.Name}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Re-index failed");
            StatusText = $"Re-index failed: {ex.Message}";
        }
    }

    private void OpenCodebaseInBrowser(WorkspaceItemDto? ws)
    {
        if (ws == null) return;
        try
        {
            var targetParam = Uri.EscapeDataString(ws.Root);
            var url = $"http://127.0.0.1:4427/dashboard/codebase?project={targetParam}";
            Cli.Wrap("cmd")
                .WithArguments($"/c start \"\" \"{url}\"")
                .ExecuteAsync()
                .GetAwaiter()
                .GetResult();
            StatusText = $"Opened codebase view for {ws.Name}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open browser URL");
            StatusText = $"Could not open browser: {ex.Message}";
        }
    }

    private async Task ConfirmDeleteAsync()
    {
        if (DeleteTarget == null) return;
        var target = DeleteTarget;
        DeleteTarget = null;
        StatusText = $"Deleting {target.Name}...";
        try
        {
            var ok = await _apiClient.DeleteProjectAsync(
                target.Root,
                deleteRegistry: DeleteRegistry,
                deleteCodebaseIndex: DeleteCodebaseIndex,
                deleteDotEling: DeleteDotEling);

            if (ok)
            {
                StatusText = $"Deleted {target.Name}";
                await LoadAsync();
            }
            else
            {
                StatusText = $"Delete failed for {target.Name}";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Delete failed");
            StatusText = $"Delete failed: {ex.Message}";
        }
    }

    private async Task AddRootAsync()
    {
        if (string.IsNullOrWhiteSpace(NewRootPath)) return;
        _logger.LogInformation("Folders add root {Path}", NewRootPath.Trim());
        try
        {
            if (await _apiClient.AddWorkspaceAsync(NewRootPath.Trim()))
            {
                SelectedRoot = NewRootPath.Trim();
                NewRootPath = "";
                await LoadAsync();
            }
            else
            {
                StatusText = "Add failed: backend rejected the path (must exist on the backend machine).";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add root");
            StatusText = $"Add failed: {ex.Message}";
        }
    }

    private async Task RemoveRootAsync(string? target)
    {
        var rootToRemove = string.IsNullOrWhiteSpace(target) ? SelectedRoot : target.Trim();
        if (string.IsNullOrEmpty(rootToRemove)) return;
        _logger.LogInformation("Folders remove root {Path}", rootToRemove);
        try
        {
            if (await _apiClient.RemoveWorkspaceAsync(rootToRemove))
            {
                if (string.Equals(SelectedRoot, rootToRemove, StringComparison.OrdinalIgnoreCase))
                {
                    SelectedRoot = "";
                }

                await LoadAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove root");
            StatusText = $"Remove failed: {ex.Message}";
        }
    }

    public string CurrentPath { get; set; } = "";
    public FileListEntryDto? SelectedEntry { get; set; }

    public async Task SwitchWorkspaceAsync(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || string.Equals(SelectedRoot, root, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SelectedRoot = root;
        CurrentPath = "";
        SelectedEntry = null;
        await LoadAsync();
    }
}
