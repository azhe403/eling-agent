using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Eling.Desktop.Services;
using Eling.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Eling.Desktop.Views;

public partial class FoldersView : UserControl
{
 public FoldersView()
 {
 InitializeComponent();
 Loaded += async (_, _) =>
 {
 if (DataContext is FoldersViewModel vm)
 {
 await vm.LoadAsync();
 }
 };

 var entriesListBox = this.FindControl<ListBox>("EntriesListBox");
 if (entriesListBox is not null)
 {
 entriesListBox.DoubleTapped += OnEntryDoubleTapped;
 }
 var rootsListBox = this.FindControl<ListBox>("RootsListBox");
 if (rootsListBox is not null)
 {
 rootsListBox.SelectionChanged += OnRootsSelectionChanged;
 }
 }

    private bool _switchingWorkspace;

    private async void OnRootsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_switchingWorkspace || e.AddedItems.Count == 0) return;
        if (DataContext is not FoldersViewModel vm) return;
        _switchingWorkspace = true;
        try { await vm.LoadAsync(); }
        finally { _switchingWorkspace = false; }
    }

 private void OnEntryDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
 {
 if (DataContext is FoldersViewModel vm && vm.SelectedEntry is { } entry)
 {
 vm.OpenEntryCommand.Execute(entry).Subscribe();
 }
 }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private async void OnBrowse(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not FoldersViewModel folders) return;
        var picked = await PickFolderAsync(folders);
        if (!string.IsNullOrWhiteSpace(picked))
            folders.NewRootPath = picked;
    }

    private async void OnAddRoot(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not FoldersViewModel vm) return;
        if (string.IsNullOrWhiteSpace(vm.NewRootPath))
        {
            var picked = await PickFolderAsync(vm);
            if (string.IsNullOrWhiteSpace(picked)) return;
            vm.NewRootPath = picked;
        }
        vm.AddRootCommand.Execute().Subscribe(
            _ => { },
            ex => vm.StatusText = $"Add failed: {ex.Message}");
    }

    private async System.Threading.Tasks.Task<string?> PickFolderAsync(FoldersViewModel folders)
    {
        try
        {
            if (Application.Current is not App app || app.Services is null)
            {
                folders.StatusText = "Browse failed: app services unavailable.";
                return null;
            }

            var browser = this.FindControl<HostBrowseView>("Browser")
                ?? this.FindControl<HostBrowseView>("Browser2");
            if (browser is null)
            {
                folders.StatusText = "Browse failed: browser control missing.";
                return null;
            }

            var apiClient = app.Services.GetRequiredService<ElingApiClient>();
            var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
            browser.DataContext = new HostBrowseViewModel(apiClient, loggerFactory.CreateLogger<HostBrowseViewModel>());

            // Dispatch load on UI thread since Loaded may have already fired before DataContext assignment
            _ = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (browser.DataContext is HostBrowseViewModel browseVm)
                    await browseVm.LoadInitialAsync();
            });

            var tcs = new System.Threading.Tasks.TaskCompletionSource<string?>();
            void OnClosed(string? p) => tcs.TrySetResult(p);

            browser.Closed += OnClosed;
            folders.ShowBrowser = true;
            try
            {
                return await tcs.Task;
            }
            finally
            {
                folders.ShowBrowser = false;
                browser.Closed -= OnClosed;
            }
        }
        catch (System.Exception ex)
        {
            folders.StatusText = $"Browse failed: {ex.Message}";
            return null;
        }
    }
}
