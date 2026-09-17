using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Eling.Desktop.ViewModels;

namespace Eling.Desktop.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        var box = this.FindControl<TextBox>("ApiKeyBox");
        if (box != null)
        {
            box.TextChanged += (_, _) =>
            {
                if (DataContext is SettingsViewModel vm)
                {
                    vm.ApiKey = box.Text ?? "";
                }
            };
        }

        if (DataContext is SettingsViewModel vm2)
        {
            _ = vm2.LoadAsync();
        }
    }
}
