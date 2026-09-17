using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Eling.Desktop.Views;

public partial class MemoriesView : UserControl
{
    public MemoriesView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
