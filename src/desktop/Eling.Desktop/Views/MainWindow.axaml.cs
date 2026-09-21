using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Eling.Desktop;
using Eling.Desktop.Services;
using Eling.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Eling.Desktop.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel? _viewModel;
    private readonly DesktopSettingsStore? _settingsStore;
    private readonly DispatcherTimer _saveDebounceTimer;
    private bool _restoring;

    public MainWindow()
    {
        InitializeComponent();
        _saveDebounceTimer = CreateSaveTimer();
    }

    public MainWindow(MainViewModel viewModel, DesktopSettingsStore settingsStore)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _settingsStore = settingsStore;
        DataContext = _viewModel;
        _saveDebounceTimer = CreateSaveTimer();

        if (Application.Current is App app && app.Services is not null)
        {
            this.FindControl<ChatView>("ChatPanel")?.DataContext =
                app.Services.GetRequiredService<ChatViewModel>();
            this.FindControl<FoldersView>("WorkspacesPanel")?.DataContext =
                app.Services.GetRequiredService<FoldersViewModel>();
            this.FindControl<SettingsView>("SettingsPanel")?.DataContext =
                app.Services.GetRequiredService<SettingsViewModel>();
        }

        RestoreWindowBounds();
    }

    private void ShowOnly(Control? panel)
    {
        foreach (var name in new[] { "ChatPanel", "WorkspacesPanel", "MemoryPanel", "SettingsPanel" })
        {
            if (this.FindControl<Control>(name) is { } control)
                control.IsVisible = control == panel;
        }

        if (panel == this.FindControl<Control>("ChatPanel"))
        {
            if (this.FindControl<ChatView>("ChatPanel")?.DataContext is ChatViewModel chatVm)
            {
                _ = chatVm.LoadAsync();
            }
        }
        else if (panel == this.FindControl<Control>("SettingsPanel"))
        {
            if (this.FindControl<SettingsView>("SettingsPanel")?.DataContext is SettingsViewModel settingsVm)
            {
                _ = settingsVm.LoadAsync();
            }
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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (_restoring || _settingsStore is null)
        {
            return;
        }

        if (change.Property.Name == "Position" ||
            change.Property.Name == "ClientSize" ||
            change.Property.Name == "WindowState")
        {
            _saveDebounceTimer.Stop();
            _saveDebounceTimer.Start();
        }
    }

    private DispatcherTimer CreateSaveTimer()
    {
        var timer = new DispatcherTimer
        {
            Interval = System.TimeSpan.FromMilliseconds(500)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            SaveWindowBounds();
        };
        return timer;
    }

    private void RestoreWindowBounds()
    {
        if (_settingsStore?.GetWindowBounds() is not { } bounds)
        {
            return;
        }

        try
        {
            _restoring = true;
            WindowStartupLocation = WindowStartupLocation.Manual;

            if (bounds.IsMaximized)
            {
                Width = bounds.Width;
                Height = bounds.Height;
                WindowState = WindowState.Maximized;
                return;
            }

            Width = bounds.Width;
            Height = bounds.Height;

            if (IsOnAnyScreen(bounds.X, bounds.Y))
            {
                Position = new PixelPoint((int)bounds.X, (int)bounds.Y);
            }
            else
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
        }
        finally
        {
            _restoring = false;
        }
    }

    private bool IsOnAnyScreen(double x, double y)
    {
        try
        {
            var screens = Screens?.All;
            if (screens is null)
            {
                return true;
            }

            foreach (var screen in screens)
            {
                if (screen.WorkingArea.Contains(new PixelPoint((int)x, (int)y)))
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return true;
        }
    }

    private void SaveWindowBounds()
    {
        if (_settingsStore is null || WindowState == WindowState.Minimized)
        {
            return;
        }

        try
        {
            if (WindowState == WindowState.Maximized)
            {
                var previous = _settingsStore.GetWindowBounds();
                var width = previous?.Width ?? Width;
                var height = previous?.Height ?? Height;
                var x = previous?.X ?? Position.X;
                var y = previous?.Y ?? Position.Y;
                _settingsStore.SaveWindowBounds(new DesktopSettingsStore.WindowBounds(x, y, width, height, true));
                return;
            }

            _settingsStore.SaveWindowBounds(
                new DesktopSettingsStore.WindowBounds(Position.X, Position.Y, Width, Height, false));
        }
        catch
        {
            // Fail-safe: never disrupt the window for a settings write.
        }
    }
}
