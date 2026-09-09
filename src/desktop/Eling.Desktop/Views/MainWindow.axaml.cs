using Avalonia.Controls;
using Avalonia.Interactivity;
using Eling.Desktop.ViewModels;

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
    }

    protected override async void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (_viewModel != null)
        {
            await _viewModel.InitializeAsync();
        }
    }
}
