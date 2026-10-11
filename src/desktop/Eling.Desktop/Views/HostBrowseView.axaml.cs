using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Eling.Desktop.ViewModels;

namespace Eling.Desktop.Views;

public partial class HostBrowseView : UserControl
{
    public event Action<string?>? Closed;

    public HostBrowseView()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (DataContext is HostBrowseViewModel vm)
            {
                await vm.LoadInitialAsync();
            }
        };
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnEntryDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not HostBrowseViewModel vm) return;
        var tapped = (e.Source as Control)?.DataContext as Models.HostEntry ?? vm.SelectedEntry;
        vm.OpenEntryCommand.Execute(tapped).Subscribe();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Closed?.Invoke(null);
    }

    private void OnUseFolder(object? sender, RoutedEventArgs e)
    {
        if (DataContext is HostBrowseViewModel vm)
        {
            var path = vm.UseThisPath;
            Closed?.Invoke(string.IsNullOrWhiteSpace(path) ? null : path);
        }
        else
        {
            Closed?.Invoke(null);
        }
    }
}
