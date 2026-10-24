using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
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

    private async Task<string?> PickFolderAsync(FoldersViewModel folders)
    {
        try
        {
            if (Application.Current is not App app || app.Services is null)
            {
                folders.StatusText = "Browse failed: app services unavailable.";
                return null;
            }

            var browser = this.FindControl<HostBrowseView>("Browser");
            if (browser is null)
            {
                folders.StatusText = "Browse failed: browser control missing.";
                return null;
            }

            var apiClient = app.Services.GetRequiredService<ElingApiClient>();
            var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
            browser.DataContext = new HostBrowseViewModel(apiClient, loggerFactory.CreateLogger<HostBrowseViewModel>());

            _ = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                try
                {
                    if (browser.DataContext is HostBrowseViewModel browseVm)
                        await browseVm.LoadInitialAsync();
                }
                catch (Exception ex)
                {
                    folders.StatusText = $"Browse load failed: {ex.Message}";
                }
            });

            var tcs = new TaskCompletionSource<string?>();
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
        catch (Exception ex)
        {
            folders.StatusText = $"Browse failed: {ex.Message}";
            folders.ShowBrowser = false;
            return null;
        }
    }
}
