using System.Collections.Specialized;
using System.Reactive.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Eling.Desktop.ViewModels;

namespace Eling.Desktop.Views;

public partial class ChatView : UserControl
{
    private ScrollViewer? _scrollViewer;
    private ToggleSwitch? _autoScrollToggle;
    private bool _programmaticScroll;

    private bool AutoScrollEnabled => _autoScrollToggle?.IsChecked ?? true;

    public ChatView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _scrollViewer = this.FindControl<ScrollViewer>("MessagesScrollViewer");
        _autoScrollToggle = this.FindControl<ToggleSwitch>("AutoScrollToggle");

        if (_scrollViewer is not null)
            _scrollViewer.ScrollChanged += OnScrollChanged;
        if (_autoScrollToggle is not null)
            _autoScrollToggle.IsCheckedChanged += OnAutoScrollToggled;

        var inputBox = this.FindControl<TextBox>("InputBox");
        inputBox?.AddHandler(
            InputElement.KeyDownEvent,
            OnInputKeyDown,
            Avalonia.Interactivity.RoutingStrategies.Bubble,
            handledEventsToo: true);

        if (DataContext is ChatViewModel vm)
        {
            vm.Messages.CollectionChanged += OnMessagesChanged;
            _ = vm.LoadAsync();
            Dispatcher.UIThread.Post(ScrollToBottom);
        }
    }

    private void OnUnloaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_scrollViewer is not null)
            _scrollViewer.ScrollChanged -= OnScrollChanged;
        if (_autoScrollToggle is not null)
            _autoScrollToggle.IsCheckedChanged -= OnAutoScrollToggled;
        if (DataContext is ChatViewModel vm)
            vm.Messages.CollectionChanged -= OnMessagesChanged;
    }

    private void OnAutoScrollToggled(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_autoScrollToggle?.IsChecked == true)
        {
            ScrollToBottom();
        }
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_scrollViewer is null || _programmaticScroll) return;
        var atBottom = _scrollViewer.Offset.Y >= _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height - 1;
        if (_autoScrollToggle is not null && !atBottom && e.OffsetDelta.Y < -0.5)
        {
            _autoScrollToggle.IsChecked = false;
        }
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (AutoScrollEnabled)
        {
            Dispatcher.UIThread.Post(ScrollToBottom);
        }
    }

    private void ScrollToBottom()
    {
        if (_scrollViewer is null) return;
        _programmaticScroll = true;
        _scrollViewer.ScrollToEnd();
        Dispatcher.UIThread.Post(() => _programmaticScroll = false);
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
        if (DataContext is not ChatViewModel vm || vm.IsBusy) return;
        e.Handled = true;
        _ = vm.SendCommand.Execute().ToTask();
    }
}
