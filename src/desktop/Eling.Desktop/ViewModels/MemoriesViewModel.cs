using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Eling.Desktop.Models;
using Eling.Desktop.Services;
using Microsoft.Extensions.Logging;
using ReactiveUI;

namespace Eling.Desktop.ViewModels;

public class MemoriesViewModel : ViewModelBase, IDisposable
{
    private readonly ElingApiClient _apiClient;
    private readonly ILogger<MemoriesViewModel> _logger;
    private CancellationTokenSource? _cts;

    private bool _isLoading;
    private string _searchQuery = "";
    private string _selectedType = "All";
    private string _selectedScope = "all";

    // Edit states
    private MemoryDto? _editingMemory;
    private string _editContent = "";
    private string _editType = "Note";
    private string _editStatus = "Active";

    // Dialog states
    private MemoryDto? _deleteTarget;
    private MemoryDto? _promoteTarget;
    private bool _promoteAsMove;
    private MemoryDto? _copyTarget;
    private bool _copyAsMove;
    private string _copyProjectRoot = "";
    private bool _showCreateDialog;
    private string _createContent = "";
    private string _createType = "Note";
    private string _createTags = "";
    private string _createScope = "all";

    public bool IsLoading { get => _isLoading; set => this.RaiseAndSetIfChanged(ref _isLoading, value); }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            this.RaiseAndSetIfChanged(ref _searchQuery, value);
            UpdateFilteredMemories();
        }
    }

    public string SelectedType
    {
        get => _selectedType;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedType, value);
            UpdateFilteredMemories();
        }
    }

    public string SelectedScope
    {
        get => _selectedScope;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedScope, value);
            _ = LoadMemoriesAsync();
        }
    }

    // Edit properties
    public MemoryDto? EditingMemory { get => _editingMemory; set => this.RaiseAndSetIfChanged(ref _editingMemory, value); }
    public string EditContent { get => _editContent; set => this.RaiseAndSetIfChanged(ref _editContent, value); }
    public string EditType { get => _editType; set => this.RaiseAndSetIfChanged(ref _editType, value); }
    public string EditStatus { get => _editStatus; set => this.RaiseAndSetIfChanged(ref _editStatus, value); }

    // Dialog properties
    public MemoryDto? DeleteTarget { get => _deleteTarget; set => this.RaiseAndSetIfChanged(ref _deleteTarget, value); }
    public MemoryDto? PromoteTarget { get => _promoteTarget; set => this.RaiseAndSetIfChanged(ref _promoteTarget, value); }
    public bool PromoteAsMove { get => _promoteAsMove; set => this.RaiseAndSetIfChanged(ref _promoteAsMove, value); }
    public MemoryDto? CopyTarget { get => _copyTarget; set => this.RaiseAndSetIfChanged(ref _copyTarget, value); }
    public bool CopyAsMove { get => _copyAsMove; set => this.RaiseAndSetIfChanged(ref _copyAsMove, value); }
    public string CopyProjectRoot { get => _copyProjectRoot; set => this.RaiseAndSetIfChanged(ref _copyProjectRoot, value); }
    public bool ShowCreateDialog { get => _showCreateDialog; set => this.RaiseAndSetIfChanged(ref _showCreateDialog, value); }
    public string CreateContent { get => _createContent; set => this.RaiseAndSetIfChanged(ref _createContent, value); }
    public string CreateType { get => _createType; set => this.RaiseAndSetIfChanged(ref _createType, value); }
    public string CreateTags { get => _createTags; set => this.RaiseAndSetIfChanged(ref _createTags, value); }
    public string CreateScope { get => _createScope; set => this.RaiseAndSetIfChanged(ref _createScope, value); }

    public int TotalMemoriesCount => Memories.Count;
    public int FilteredMemoriesCount => FilteredMemories.Count;
    public string MemoryCountStatus => $"Showing {FilteredMemoriesCount} of {TotalMemoriesCount} memories";

    public ObservableCollection<MemoryDto> Memories { get; } = [];
    public ObservableCollection<MemoryDto> FilteredMemories { get; } = [];
    public ObservableCollection<RuntimeDto> Runtimes { get; } = [];

    public string[] TypeFilters { get; } = ["All", "Fact", "Preference", "Decision", "Lesson", "Note"];
    public string[] TypeOptions { get; } = ["Fact", "Preference", "Decision", "Lesson", "Note"];
    public string[] StatusOptions { get; } = ["Active", "Superseded", "Archived"];

    // Commands
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> RefreshCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ClearSearchCommand { get; }
    public ReactiveCommand<string, System.Reactive.Unit> SelectScopeCommand { get; }
    public ReactiveCommand<string, System.Reactive.Unit> SelectTypeCommand { get; }

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

    public MemoriesViewModel(
        ElingApiClient apiClient,
        ILogger<MemoriesViewModel> logger)
    {
        _apiClient = apiClient;
        _logger = logger;

        RefreshCommand = ReactiveCommand.CreateFromTask(() => LoadMemoriesAsync());
        ClearSearchCommand = ReactiveCommand.Create(() => { SearchQuery = ""; });
        SelectScopeCommand = ReactiveCommand.Create<string>(scope => { SelectedScope = scope ?? "all"; });
        SelectTypeCommand = ReactiveCommand.Create<string>(type => { SelectedType = type ?? "All"; });

        CreateMemoryCommand = ReactiveCommand.Create(() => { ShowCreateDialog = true; });
        SaveCreateCommand = ReactiveCommand.CreateFromTask(() => CreateMemoryAsync());
        CancelCreateCommand = ReactiveCommand.Create(() => { ShowCreateDialog = false; });

        StartEditCommand = ReactiveCommand.Create<MemoryDto>(m =>
        {
            if (m == null) return;
            StartEdit(m);
        });
        SaveEditCommand = ReactiveCommand.CreateFromTask(() => SaveEditAsync());
        CancelEditCommand = ReactiveCommand.Create(() => { EditingMemory = null; });

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
            CopyProjectRoot = Runtimes.FirstOrDefault()?.ProjectRoot ?? "";
        });
        ConfirmCopyCommand = ReactiveCommand.CreateFromTask(() => CopyToProjectAsync());
        CancelCopyCommand = ReactiveCommand.Create(() => { CopyTarget = null; });
    }

    public async Task LoadMemoriesAsync()
    {
        IsLoading = true;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        try
        {
            _logger.LogInformation("Loading memories (scope={Scope})...", SelectedScope);
            var memories = await _apiClient.GetMemoriesAsync(SelectedScope, limit: 100, _cts.Token);
            Memories.Clear();
            foreach (var memory in memories)
            {
                Memories.Add(memory);
            }

            UpdateFilteredMemories();
            _logger.LogInformation("Loaded {Count} memories", Memories.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load memories");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void UpdateFilteredMemories()
    {
        var filtered = Memories.Where(m =>
            (SelectedType == "All" || string.Equals(m.Type, SelectedType, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(SearchQuery) ||
             m.Content.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase) ||
             m.Id.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(m => m.UpdatedAt)
            .ThenByDescending(m => m.CreatedAt)
            .ToList();

        FilteredMemories.Clear();
        foreach (var item in filtered)
        {
            FilteredMemories.Add(item);
        }

        this.RaisePropertyChanged(nameof(TotalMemoriesCount));
        this.RaisePropertyChanged(nameof(FilteredMemoriesCount));
        this.RaisePropertyChanged(nameof(MemoryCountStatus));
    }

    public async Task LoadRuntimesAsync()
    {
        try
        {
            var runtimes = await _apiClient.GetRuntimesAsync();
            Runtimes.Clear();
            foreach (var r in runtimes)
            {
                Runtimes.Add(r);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load runtimes");
        }
    }

    private async Task CreateMemoryAsync()
    {
        if (string.IsNullOrWhiteSpace(CreateContent)) return;
        _logger.LogInformation("Memories create type={Type} scope={Scope}", CreateType, CreateScope);
        try
        {
            var tags = CreateTags.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).ToList();
            var result = await _apiClient.CreateMemoryAsync(CreateContent.Trim(), CreateType, tags, CreateScope);
            if (result != null)
            {
                Memories.Insert(0, result);
                UpdateFilteredMemories();
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
        _logger.LogInformation("Memories save edit id={Id}", EditingMemory.Id);
        try
        {
            var result = await _apiClient.UpdateMemoryAsync(
                EditingMemory.Id, EditContent, EditType, EditStatus, EditingMemory.Scope ?? "all");

            if (result != null)
            {
                var index = Memories.ToList().FindIndex(m => m.Id == EditingMemory.Id);
                if (index >= 0)
                {
                    Memories[index] = result;
                    UpdateFilteredMemories();
                }
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
        _logger.LogInformation("Memories delete id={Id}", DeleteTarget.Id);
        try
        {
            if (await _apiClient.DeleteMemoryAsync(DeleteTarget.Id, DeleteTarget.Scope ?? "all"))
            {
                Memories.Remove(DeleteTarget);
                UpdateFilteredMemories();
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
        _logger.LogInformation("Memories promote id={Id} move={Move}", PromoteTarget.Id, PromoteAsMove);
        try
        {
            var projectRoot = PromoteTarget.Project?.Root ?? "";
            if (await _apiClient.PromoteToGlobalAsync(PromoteTarget.Id, projectRoot, PromoteAsMove))
            {
                await LoadMemoriesAsync();
                PromoteTarget = null;
                PromoteAsMove = false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to promote memory");
        }
    }

    private async Task CopyToProjectAsync()
    {
        if (CopyTarget == null || string.IsNullOrEmpty(CopyProjectRoot)) return;
        _logger.LogInformation("Memories copy id={Id} target={Target} move={Move}", CopyTarget.Id, CopyProjectRoot, CopyAsMove);
        try
        {
            if (await _apiClient.CopyToProjectAsync(CopyTarget.Id, CopyTarget.Scope ?? "global", CopyTarget.Project?.Root, CopyProjectRoot, CopyAsMove))
            {
                await LoadMemoriesAsync();
                CopyTarget = null;
                CopyAsMove = false;
                CopyProjectRoot = "";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to copy memory");
        }
    }

    public void StartEdit(MemoryDto memory)
    {
        EditingMemory = memory;
        EditContent = memory.Content;
        EditType = memory.Type;
        EditStatus = memory.Status;
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException ex) { _logger.LogDebug(ex, "Cancellation source already disposed during shutdown"); }
        try { _cts?.Dispose(); } catch (ObjectDisposedException ex) { _logger.LogDebug(ex, "Cancellation source already disposed during shutdown"); }
    }
}
