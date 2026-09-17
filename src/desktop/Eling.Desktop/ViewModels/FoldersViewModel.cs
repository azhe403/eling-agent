using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Eling.Desktop.Models;
using Eling.Desktop.Services;
using Microsoft.Extensions.Logging;
using ReactiveUI;

namespace Eling.Desktop.ViewModels;

public class FoldersViewModel : ViewModelBase
{
    private readonly ElingApiClient _apiClient;
    private readonly ILogger<FoldersViewModel> _logger;

    private string _selectedRoot = "";
    private string _currentPath = "";
    private string _previewText = "";
    private string _editText = "";
    private bool _isEditing;
    private string _statusText = "";
    private string _newRootPath = "";
    private FileListEntryDto? _selectedEntry;
    private bool _showBrowser;

    public string SelectedRoot { get => _selectedRoot; set => this.RaiseAndSetIfChanged(ref _selectedRoot, value); }
    public string CurrentPath { get => _currentPath; set => this.RaiseAndSetIfChanged(ref _currentPath, value); }
    public string PreviewText { get => _previewText; set => this.RaiseAndSetIfChanged(ref _previewText, value); }
    public string EditText { get => _editText; set => this.RaiseAndSetIfChanged(ref _editText, value); }
    public bool IsEditing { get => _isEditing; set => this.RaiseAndSetIfChanged(ref _isEditing, value); }
    public string StatusText { get => _statusText; set => this.RaiseAndSetIfChanged(ref _statusText, value); }
    public string NewRootPath { get => _newRootPath; set => this.RaiseAndSetIfChanged(ref _newRootPath, value); }
    public FileListEntryDto? SelectedEntry { get => _selectedEntry; set => this.RaiseAndSetIfChanged(ref _selectedEntry, value); }
    public bool ShowBrowser { get => _showBrowser; set => this.RaiseAndSetIfChanged(ref _showBrowser, value); }

    public ObservableCollection<string> Roots { get; } = [];
    public ObservableCollection<FileListEntryDto> Entries { get; } = [];

    public bool CanGoUp => !string.IsNullOrEmpty(CurrentPath);

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> LoadCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> AddRootCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> RemoveRootCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> RefreshCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> GoUpCommand { get; }
    public ReactiveCommand<FileListEntryDto, System.Reactive.Unit> OpenEntryCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> StartEditCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> SaveEditCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> CancelEditCommand { get; }

    public FoldersViewModel(
        ElingApiClient apiClient,
        ILogger<FoldersViewModel> logger)
    {
        _apiClient = apiClient;
        _logger = logger;

        LoadCommand = ReactiveCommand.CreateFromTask(() => LoadAsync());
        AddRootCommand = ReactiveCommand.CreateFromTask(() => AddRootAsync());
        RemoveRootCommand = ReactiveCommand.CreateFromTask(() => RemoveRootAsync());
        RefreshCommand = ReactiveCommand.CreateFromTask(() => ListCurrentAsync());
        GoUpCommand = ReactiveCommand.CreateFromTask(() => GoUpAsync());
        OpenEntryCommand = ReactiveCommand.CreateFromTask<FileListEntryDto>(entry => OpenEntryAsync(entry));
        StartEditCommand = ReactiveCommand.Create(() => { IsEditing = true; EditText = PreviewText; });
        SaveEditCommand = ReactiveCommand.CreateFromTask(() => SaveEditAsync());
        CancelEditCommand = ReactiveCommand.Create(() => { IsEditing = false; });
    }

    public async Task LoadAsync()
    {
        try
        {
            var roots = await _apiClient.GetWorkspacesAsync();
            Roots.Clear();
            foreach (var root in roots)
            {
                Roots.Add(root);
            }

            if (!Roots.Contains(SelectedRoot))
            {
                SelectedRoot = Roots.FirstOrDefault() ?? "";
                CurrentPath = "";
            }

            if (!string.IsNullOrEmpty(SelectedRoot))
            {
                await ListCurrentAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load folders");
            StatusText = $"Load failed: {ex.Message}";
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
                CurrentPath = "";
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

    private async Task RemoveRootAsync()
    {
        if (string.IsNullOrEmpty(SelectedRoot)) return;
        _logger.LogInformation("Folders remove root {Path}", SelectedRoot);
        try
        {
            if (await _apiClient.RemoveWorkspaceAsync(SelectedRoot))
            {
                SelectedRoot = "";
                CurrentPath = "";
                Entries.Clear();
                PreviewText = "";
                await LoadAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove root");
            StatusText = $"Remove failed: {ex.Message}";
        }
    }

    private async Task GoUpAsync()
    {
        if (string.IsNullOrEmpty(CurrentPath)) return;
        var cut = CurrentPath.LastIndexOf('/');
        if (cut < 0) cut = CurrentPath.LastIndexOf('\\');
        CurrentPath = cut < 0 ? "" : CurrentPath[..cut];
        this.RaisePropertyChanged(nameof(CanGoUp));
        await ListCurrentAsync();
    }

    private async Task OpenEntryAsync(FileListEntryDto? entry)
    {
        if (entry == null || string.IsNullOrEmpty(SelectedRoot)) return;
        if (!entry.IsDirectory)
        {
            await PreviewEntryAsync(entry);
            return;
        }

        CurrentPath = string.IsNullOrEmpty(CurrentPath) ? entry.Path : CurrentPath + "/" + entry.Path;
        this.RaisePropertyChanged(nameof(CanGoUp));
        await ListCurrentAsync();
    }

    private async Task PreviewEntryAsync(FileListEntryDto entry)
    {
        if (string.IsNullOrEmpty(SelectedRoot)) return;
        var rel = string.IsNullOrEmpty(CurrentPath) ? entry.Path : CurrentPath + "/" + entry.Path;
        _logger.LogInformation("Folders preview {Path}", rel);
        IsEditing = false;
        try
        {
            var file = await _apiClient.ReadFileAsync(SelectedRoot, rel);
            if (file == null)
            {
                StatusText = "Preview failed: file is binary or oversize.";
                return;
            }

            PreviewText = file.Content + (file.Truncated ? "\n\n... (truncated)" : "");
            StatusText = rel;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to preview file");
            StatusText = $"Preview failed: {ex.Message}";
        }
    }

    private async Task SaveEditAsync()
    {
        if (string.IsNullOrEmpty(SelectedRoot) || SelectedEntry == null) return;
        var rel = string.IsNullOrEmpty(CurrentPath) ? SelectedEntry.Path : CurrentPath + "/" + SelectedEntry.Path;
        _logger.LogInformation("Folders save {Path}", rel);
        try
        {
            if (await _apiClient.WriteFileAsync(SelectedRoot, rel, EditText))
            {
                IsEditing = false;
                StatusText = "Saved " + rel;
            }
            else
            {
                StatusText = "Save failed.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save file");
            StatusText = $"Save failed: {ex.Message}";
        }
    }

    private async Task ListCurrentAsync()
    {
        if (string.IsNullOrEmpty(SelectedRoot))
        {
            Entries.Clear();
            return;
        }

        try
        {
            var entries = await _apiClient.ListDirAsync(SelectedRoot, CurrentPath);
            Entries.Clear();
            foreach (var entry in entries)
            {
                Entries.Add(entry);
            }

            this.RaisePropertyChanged(nameof(CanGoUp));
            StatusText = string.IsNullOrEmpty(CurrentPath) ? SelectedRoot : CurrentPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list directory");
            StatusText = $"List failed: {ex.Message}";
        }
    }
}
