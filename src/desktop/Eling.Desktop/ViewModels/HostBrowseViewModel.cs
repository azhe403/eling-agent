using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Eling.Desktop.Services;
using Microsoft.Extensions.Logging;
using ReactiveUI;

namespace Eling.Desktop.ViewModels;

public class HostEntry(string displayName, string fullPath, bool isDirectory)
{
    public string DisplayName { get; } = displayName;
    public string FullPath { get; } = fullPath;
    public bool IsDirectory { get; } = isDirectory;
}

public class HostBrowseViewModel : ViewModelBase
{
    private readonly ElingApiClient _apiClient;
    private readonly ILogger<HostBrowseViewModel> _logger;

    private string _currentPath = "";
    private string? _parentPath;
    private string _statusText = "";
    private HostEntry? _selectedEntry;

    public string CurrentPath { get => _currentPath; set => this.RaiseAndSetIfChanged(ref _currentPath, value); }
    public string StatusText { get => _statusText; set => this.RaiseAndSetIfChanged(ref _statusText, value); }
    public HostEntry? SelectedEntry { get => _selectedEntry; set => this.RaiseAndSetIfChanged(ref _selectedEntry, value); }

    public ObservableCollection<HostEntry> Entries { get; } = [];

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> LoadCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> UpCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> OpenSelectedCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> GoCommand { get; }

    public HostBrowseViewModel(
        ElingApiClient apiClient,
        ILogger<HostBrowseViewModel> logger)
    {
        _apiClient = apiClient;
        _logger = logger;

        LoadCommand = ReactiveCommand.CreateFromTask(() => LoadInitialAsync());
        UpCommand = ReactiveCommand.CreateFromTask(() => GoToAsync(_parentPath));
        OpenSelectedCommand = ReactiveCommand.CreateFromTask(() => OpenSelectedAsync());
        GoCommand = ReactiveCommand.CreateFromTask(() => GoToAsync(CurrentPath));
    }

    public async Task LoadInitialAsync()
    {
        _logger.LogInformation("HostBrowse load drives");
        try
        {
            var drives = await _apiClient.GetHostDrivesAsync();
            Entries.Clear();
            foreach (var drive in drives)
            {
                Entries.Add(new HostEntry(drive, drive, true));
            }

            CurrentPath = "";
            _parentPath = null;
            StatusText = drives.Count == 0 ? "No drives returned by backend." : "";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load host drives");
            StatusText = $"Load failed: {ex.Message}";
        }
    }

    private async Task OpenSelectedAsync()
    {
        if (SelectedEntry == null || !SelectedEntry.IsDirectory) return;
        _logger.LogInformation("HostBrowse open {Path}", SelectedEntry.FullPath);
        await GoToAsync(SelectedEntry.FullPath);
    }

    private async Task GoToAsync(string? path)
    {
        _logger.LogInformation("HostBrowse go {Path}", string.IsNullOrWhiteSpace(path) ? "(drives)" : path);
        if (string.IsNullOrWhiteSpace(path))
        {
            await LoadInitialAsync();
            return;
        }

        try
        {
            var result = await _apiClient.BrowseHostAsync(path);
            if (result == null)
            {
                StatusText = "Cannot open that folder.";
                return;
            }

            CurrentPath = result.Path;
            _parentPath = result.Parent;
            Entries.Clear();
            foreach (var entry in result.Entries)
            {
                Entries.Add(new HostEntry(DisplayNameOf(entry.Path), entry.Path, entry.IsDirectory));
            }

            StatusText = "";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to browse host path");
            StatusText = $"Browse failed: {ex.Message}";
        }
    }

    private static string DisplayNameOf(string fullPath)
    {
        var trimmed = fullPath.TrimEnd('/', '\\');
        var slash = trimmed.LastIndexOf('/');
        var backslash = trimmed.LastIndexOf('\\');
        var cut = Math.Max(slash, backslash);
        return cut < 0 ? trimmed : trimmed[(cut + 1)..];
    }
}
