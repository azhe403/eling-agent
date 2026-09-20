using System;
using System.Collections.Specialized;
using System.ComponentModel;
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
    private INotifyCollectionChanged? _currentMessagesCollection;
    private ChatViewModel? _currentViewModel;
    private bool _programmaticScroll;

    private bool AutoScrollEnabled => _autoScrollToggle?.IsChecked ?? true;

    public ChatView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        BindViewModel(DataContext as ChatViewModel);
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

        BindViewModel(DataContext as ChatViewModel);
        if (_currentViewModel is not null)
        {
            _ = _currentViewModel.LoadAsync();
            Dispatcher.UIThread.Post(ScrollToBottom, DispatcherPriority.Loaded);
        }
    }

    private void OnUnloaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_scrollViewer is not null)
            _scrollViewer.ScrollChanged -= OnScrollChanged;
        if (_autoScrollToggle is not null)
            _autoScrollToggle.IsCheckedChanged -= OnAutoScrollToggled;

        UnbindViewModel();
    }

    private void BindViewModel(ChatViewModel? vm)
    {
        if (ReferenceEquals(_currentViewModel, vm))
        {
            UpdateMessagesSubscription(vm?.Messages);
            return;
        }

        UnbindViewModel();
        _currentViewModel = vm;

        if (_currentViewModel is not null)
        {
            _currentViewModel.PropertyChanged += OnViewModelPropertyChanged;
            UpdateMessagesSubscription(_currentViewModel.Messages);
        }
    }

    private void UnbindViewModel()
    {
        if (_currentViewModel is not null)
        {
            _currentViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _currentViewModel = null;
        }
        UpdateMessagesSubscription(null);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatViewModel.Messages) && _currentViewModel is not null)
        {
            UpdateMessagesSubscription(_currentViewModel.Messages);
            if (AutoScrollEnabled)
            {
                Dispatcher.UIThread.Post(ScrollToBottom, DispatcherPriority.Loaded);
            }
        }
    }

    private void UpdateMessagesSubscription(INotifyCollectionChanged? newCollection)
    {
        if (ReferenceEquals(_currentMessagesCollection, newCollection)) return;

        if (_currentMessagesCollection is not null)
        {
            _currentMessagesCollection.CollectionChanged -= OnMessagesChanged;
        }

        _currentMessagesCollection = newCollection;

        if (_currentMessagesCollection is not null)
        {
            _currentMessagesCollection.CollectionChanged += OnMessagesChanged;
        }
    }

    private void OnAutoScrollToggled(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_autoScrollToggle?.IsChecked == true)
        {
            Dispatcher.UIThread.Post(ScrollToBottom, DispatcherPriority.Loaded);
        }
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_scrollViewer is null || _programmaticScroll) return;

        var maxScroll = Math.Max(0, _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height);
        var atBottom = _scrollViewer.Offset.Y >= maxScroll - 5;

        if (_autoScrollToggle is not null && !atBottom && e.OffsetDelta.Y < -0.5)
        {
            _autoScrollToggle.IsChecked = false;
        }
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (AutoScrollEnabled)
        {
            Dispatcher.UIThread.Post(ScrollToBottom, DispatcherPriority.Loaded);
        }
    }

    private void ScrollToBottom()
    {
        if (_scrollViewer is null) return;
        _programmaticScroll = true;
        _scrollViewer.ScrollToEnd();
        Dispatcher.UIThread.Post(() => _programmaticScroll = false, DispatcherPriority.Background);
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
        if (DataContext is not ChatViewModel vm || vm.IsBusy) return;
        e.Handled = true;
        _ = vm.SendCommand.Execute().ToTask();
    }
}
