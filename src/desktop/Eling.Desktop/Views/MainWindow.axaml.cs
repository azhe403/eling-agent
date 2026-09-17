using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Eling.Desktop;
using Eling.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Eling.Desktop.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;

        if (Application.Current is App app && app.Services is not null)
        {
            this.FindControl<ChatView>("ChatPanel")?.DataContext =
                app.Services.GetRequiredService<ChatViewModel>();
            this.FindControl<FoldersView>("WorkspacesPanel")?.DataContext =
                app.Services.GetRequiredService<FoldersViewModel>();
            this.FindControl<SettingsView>("SettingsPanel")?.DataContext =
                app.Services.GetRequiredService<SettingsViewModel>();
        }
    }

    private void ShowOnly(Control? panel)
    {
        foreach (var name in new[] { "ChatPanel", "WorkspacesPanel", "MemoryPanel", "SettingsPanel" })
        {
            if (this.FindControl<Control>(name) is { } control)
                control.IsVisible = control == panel;
        }
    }

    private void SidebarChat_Click(object? sender, RoutedEventArgs e) =>
        ShowOnly(this.FindControl<Control>("ChatPanel"));

    private void SidebarWorkspaces_Click(object? sender, RoutedEventArgs e) =>
        ShowOnly(this.FindControl<Control>("WorkspacesPanel"));

    private void SidebarMemory_Click(object? sender, RoutedEventArgs e) =>
        ShowOnly(this.FindControl<Control>("MemoryPanel"));

    private void SidebarSettings_Click(object? sender, RoutedEventArgs e) =>
        ShowOnly(this.FindControl<Control>("SettingsPanel"));

    protected override async void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (_viewModel != null)
        {
            await _viewModel.InitializeAsync();
        }
    }
}
