using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Eling.Desktop.Formatting;
using Eling.Desktop.Models;
using Eling.Desktop.Services;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace Eling.Desktop.ViewModels;

public class ChatViewModel : ViewModelBase, IDisposable
{
    private readonly ElingApiClient _apiClient;
    private readonly ILogger<ChatViewModel> _logger;
    private readonly Func<Action, bool> _uiDispatcher;
    private readonly Dictionary<string, ChatSession> _sessions = [];
    private ChatSession _current;

    private string _activeWorkspace = "";
    private string _input = "";
    private string _statusText = "";
    private SidebarRow? _selectedRow;
    private string _selectedModel = "";
    private Avalonia.Threading.DispatcherTimer? _elapsedTimer;
    private DateTime _turnStartedUtc;
    private BusyPhase _phase = BusyPhase.Idle;

    /// <summary>Workspaces whose sidebar chat list is expanded beyond the default five.</summary>
    private readonly HashSet<string> _expandedSidebarWorkspaces = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Last fetched chat list per workspace, so expanding/collapsing the sidebar does not hit the network.</summary>
    private readonly Dictionary<string, List<ChatSummaryDto>> _sidebarChatByWorkspace = new(StringComparer.OrdinalIgnoreCase);

    private enum BusyPhase { Idle, Waiting, Tool, Writing, Done }

    public string SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (_selectedModel == value) return;
            this.RaiseAndSetIfChanged(ref _selectedModel, value);
            _ = SaveModelAsync(value);
        }
    }

    public string ActiveWorkspace
    {
        get => _activeWorkspace;
        set
        {
            if (_activeWorkspace == value) return;
            _logger.LogInformation("Chat workspace switched to {Workspace}", value);
            this.RaiseAndSetIfChanged(ref _activeWorkspace, value);
            this.RaisePropertyChanged(nameof(FolderDisplayName));
        }
    }

    public string Input { get => _input; set => this.RaiseAndSetIfChanged(ref _input, value); }

    public bool IsBusy => _current.IsBusy;

    public string StatusText { get => _statusText; set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    public ObservableCollection<string> Workspaces { get; } = [];
    public ObservableCollection<ChatMessageRow> Messages => _current.Items;
    public ObservableCollection<SidebarRow> SidebarItems { get; } = [];
    public ObservableCollection<string> Models { get; } = [];

    public string FolderDisplayName => FolderNameOf(ActiveWorkspace);

    public SidebarRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedRow, value);
            if (value == null) return;
            if (value.IsMoreRow)
            {
                ToggleSidebarMore(value.Workspace);
                return;
            }
            if (value.IsChat && value.ChatId != null)
            {
                SwitchToChat(value.Workspace, value.ChatId);
            }
            else
            {
                SwitchToWorkspace(value.Workspace);
            }
        }
    }

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> LoadCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> SendCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> RetryCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> NewChatCommand { get; }
    public ReactiveCommand<string, System.Reactive.Unit> NewChatInWorkspaceCommand { get; }
    public ReactiveCommand<SidebarRow, System.Reactive.Unit> PromptDeleteChatCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ConfirmDeleteChatCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> CancelDeleteChatCommand { get; }

    public ChatViewModel(
        ElingApiClient apiClient,
        ILogger<ChatViewModel> logger,
        Func<Action, bool>? uiDispatcher = null)
    {
        _apiClient = apiClient;
        _logger = logger;
        _current = new ChatSession("", null);
        // Streaming row updates must land on the UI thread in production, where
        // the dispatcher runs a message loop. Tests have no loop, so a Post there
        // is silently dropped; injecting the hook lets a test run the action
        // inline and keeps this class free of timing luck. Default keeps the
        // exact production behaviour (inline when already on the UI thread,
        // otherwise Post).
        _uiDispatcher = uiDispatcher ?? (action =>
        {
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                action();
                return true;
            }

            Avalonia.Threading.Dispatcher.UIThread.Post(action);
            return false;
        });

        LoadCommand = ReactiveCommand.CreateFromTask(() => LoadAsync());
        SendCommand = ReactiveCommand.CreateFromTask(() => SendAsync());
        RetryCommand = ReactiveCommand.CreateFromTask(RetryAsync);
        NewChatCommand = ReactiveCommand.Create(() =>
        {
            StartChatIn(ActiveWorkspace);
        });
        NewChatInWorkspaceCommand = ReactiveCommand.Create<string>(workspace =>
        {
            StartChatIn(workspace);
        });
        PromptDeleteChatCommand = ReactiveCommand.Create<SidebarRow>(PromptDeleteChat);
        ConfirmDeleteChatCommand = ReactiveCommand.CreateFromTask(ConfirmDeleteChatAsync);
        CancelDeleteChatCommand = ReactiveCommand.Create(() =>
        {
            _pendingDeleteRow = null;
            IsDeletePromptOpen = false;
        });
    }

    /// <summary>Retry budget for the first load, so a backend that is still booting does not look like an empty workspace.</summary>
    public int ApiLoadRetryAttempts { get; set; } = BackendResilience.DefaultAttempts;

    public TimeSpan ApiLoadRetryDelay { get; set; } = BackendResilience.BaseDelay;

    public TimeSpan ApiLoadRetryMaxDelay { get; set; } = BackendResilience.MaxDelay;

    public async Task LoadAsync()
    {
        try
        {
            // The view loads before the backend it needs has finished starting, so the
            // first attempt usually fails. Retry only while the API reports it could not
            // reach the backend at all: a backend that answers with no workspaces is an
            // answer, and retrying it would just delay showing an empty sidebar.
            var pipeline = BackendResilience.ForResult<IReadOnlyList<string>?>(
                static roots => roots is null,
                _logger,
                ApiLoadRetryAttempts,
                ApiLoadRetryDelay,
                ApiLoadRetryMaxDelay,
                "chat-load");

            var roots = await pipeline.ExecuteAsync<IReadOnlyList<string>?>(
                _ => new ValueTask<IReadOnlyList<string>?>(_apiClient.GetWorkspacesAsync()));
            if (roots is null)
            {
                StatusText = "Load failed: the backend did not become reachable";
                return;
            }

            // Only adopt a workspace while nothing is open yet. The chat panel is
            // re-shown on every navigation and each one re-runs this load, so
            // starting a chat unconditionally wiped the transcript and discarded
            // the unsent draft every time the user switched away and back.
            if (_current.Workspace.Length == 0)
            {
                ActiveWorkspace = roots.FirstOrDefault() ?? "";
                StartChatIn(ActiveWorkspace);
            }

            await RefreshSidebarAsync();
            await LoadModelsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load chat workspaces");
            StatusText = $"Load failed: {ex.Message}";
        }
    }

    public async Task RefreshSidebarAsync()
    {
        try
        {
            var roots = await _apiClient.GetWorkspacesAsync();
            if (roots is null)
            {
                _logger.LogWarning("Skipping sidebar refresh: the backend did not respond");
                return;
            }

            Workspaces.Clear();
            foreach (var root in roots)
            {
                Workspaces.Add(root);
            }

            SidebarItems.Clear();
            foreach (var root in roots)
            {
                IReadOnlyList<ChatSummaryDto> chats = [];
                try
                {
                    chats = await _apiClient.GetChatsAsync(root);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load chats for {Workspace}", root);
                }

                _sidebarChatByWorkspace[root] = chats.ToList();
            }

            PruneSidebarCache(roots);
            RebuildSidebarFromCache();
            this.RaisePropertyChanged(nameof(FolderDisplayName));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh sidebar");
            StatusText = $"Load failed: {ex.Message}";
        }
    }

    public void StartChatIn(string workspace)
    {
        _logger.LogInformation("Chat new conversation in {Workspace}", workspace);
        ActiveWorkspace = workspace;
        Input = "";
        _selectedRow = null;
        this.RaisePropertyChanged(nameof(SelectedRow));

        var newSession = new ChatSession(workspace, null);
        SwitchToSession(newSession);
    }

    public void SwitchToWorkspace(string workspace)
    {
        _logger.LogInformation("Chat sidebar folder selected {Workspace}", workspace);
        StartChatIn(workspace);
        _selectedRow = SidebarItems.FirstOrDefault(r => r.IsFolder && r.Workspace == workspace) ?? _selectedRow;
        this.RaisePropertyChanged(nameof(SelectedRow));
    }

    private void SwitchToChat(string workspace, string chatId)
    {
        _logger.LogInformation("Chat sidebar chat selected id={Id}", chatId);
        ActiveWorkspace = workspace;
        if (!_sessions.TryGetValue(chatId, out var session))
        {
            session = new ChatSession(workspace, chatId);
            _sessions[chatId] = session;
        }

        SwitchToSession(session);
        _selectedRow = SidebarItems.FirstOrDefault(r => r.IsChat && r.ChatId == chatId) ?? _selectedRow;
        this.RaisePropertyChanged(nameof(SelectedRow));
    }

    private void SwitchToSession(ChatSession session)
    {
        _current = session;
        this.RaisePropertyChanged(nameof(Messages));
        this.RaisePropertyChanged(nameof(IsBusy));
        StatusText = session.Status;

        if (session.ChatId is { Length: > 0 } chatId && !session.HistoryRequested)
        {
            session.HistoryRequested = true;
            _ = LoadHistoryIntoAsync(session, chatId);
        }
    }

    internal static string FolderNameOf(string fullPath)
    {
        var trimmed = (fullPath ?? "").TrimEnd('/', '\\');
        if (string.IsNullOrEmpty(trimmed)) return fullPath ?? "";
        var cut = Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf('\\'));
        return cut < 0 ? trimmed : trimmed[(cut + 1)..];
    }

    private async Task LoadHistoryIntoAsync(ChatSession session, string chatId)
    {
        _logger.LogInformation("Chat history loading id={Id}", chatId);
        try
        {
            var history = await _apiClient.GetChatAsync(chatId);
            if (session.Items.Count == 0)
            {
                foreach (var message in history)
                {
                    // History arrives collapsed: one large chat can carry megabytes of
                    // tool output, and expanding every row on open built a visual for
                    // all of it at once.
                    // Tool payloads can carry megabytes of JSON; they stay collapsed.
                    // User and assistant text is the conversation itself — loading it
                    // collapsed after a restart read as "everything went blank".
                    session.Items.Add(new ChatMessageRow(
                        message.Role,
                        message.Text,
                        message.ToolName,
                        startsExpanded: !string.Equals(message.Role, "Tool", StringComparison.OrdinalIgnoreCase)));
                }
            }

            SetStatus(session, "");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load chat history");
            SetStatus(session, $"Load failed: {ex.Message}");
        }
    }

    private async Task LoadModelsAsync()
    {
        try
        {
            var provider = await _apiClient.GetProviderAsync();
            Models.Clear();
            if (provider != null)
            {
                foreach (var model in provider.ModelsCached)
                {
                    Models.Add(model);
                }

                if (!string.IsNullOrEmpty(provider.Model) && !Models.Contains(provider.Model))
                {
                    Models.Add(provider.Model);
                }

                _selectedModel = provider.Model ?? "";
                this.RaisePropertyChanged(nameof(SelectedModel));

                if (Models.Count == 0 && !string.IsNullOrWhiteSpace(provider.BaseUrl))
                {
                    _logger.LogInformation("Chat model cache empty, fetching from provider");
                    var fetched = await _apiClient.FetchModelsAsync();
                    foreach (var model in fetched)
                    {
                        Models.Add(model);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load models");
        }
    }

    private async Task SaveModelAsync(string model)
    {
        try
        {
            _logger.LogInformation("Chat model switched to {Model}", model);
            var saved = await _apiClient.UpdateProviderAsync(null, model, null);
            if (saved == null)
            {
                StatusText = "Model switch failed: backend unreachable.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to switch model");
            StatusText = $"Model switch failed: {ex.Message}";
        }
    }

    private void PruneSidebarCache(IReadOnlyList<string> roots)
    {
        var live = new HashSet<string>(roots, StringComparer.OrdinalIgnoreCase);
        foreach (var key in _sidebarChatByWorkspace.Keys.ToList())
        {
            if (!live.Contains(key))
            {
                _sidebarChatByWorkspace.Remove(key);
            }
        }
    }

    internal void RebuildSidebarFromCache()
    {
        var previousChatId = _selectedRow?.ChatId;
        SidebarItems.Clear();
        foreach (var root in Workspaces)
        {
            SidebarItems.Add(new SidebarRow(true, FolderNameOf(root), root));
            var chats = _sidebarChatByWorkspace.TryGetValue(root, out var cached) ? cached : [];
            var expanded = _expandedSidebarWorkspaces.Contains(root);
            foreach (var chat in expanded ? chats : chats.Take(5))
            {
                SidebarItems.Add(new SidebarRow(false, chat.Title, root, chat.Id, chat));
            }

            if (chats.Count > 5)
            {
                SidebarItems.Add(new SidebarRow(false, expanded
                    ? "Show less"
                    : $"+ {chats.Count - 5} more…", root, isMoreRow: true));
            }
        }

        _selectedRow = previousChatId is null
            ? null
            : SidebarItems.FirstOrDefault(r => r.IsChat && r.ChatId == previousChatId);
        this.RaisePropertyChanged(nameof(SelectedRow));
    }

    internal void ToggleSidebarMore(string workspace)
    {
        if (!_expandedSidebarWorkspaces.Remove(workspace))
        {
            _expandedSidebarWorkspaces.Add(workspace);
        }

        RebuildSidebarFromCache();
    }

    private void SetRetry(ChatSession session, string workspace, string text)
    {
        _pendingRetry = () =>
        {
            RemoveTrailingUnansweredUserRow(session);
            return SendCoreAsync(session, workspace, text);
        };
        HasRetry = true;
    }

    /// <summary>
    /// Removes the trailing run of rows from the last unanswered user row onward,
    /// so a retry re-sends the message instead of duplicating it. Stops if an
    /// assistant row sits after the user row (the turn already produced an
    /// answer; retrying is a new turn there).
    /// </summary>
    private static void RemoveTrailingUnansweredUserRow(ChatSession session)
    {
        for (var i = session.Items.Count - 1; i >= 0; i--)
        {
            if (session.Items[i].IsAssistant)
            {
                return;
            }

            if (session.Items[i].IsUser)
            {
                while (session.Items.Count > i)
                {
                    session.Items.RemoveAt(session.Items.Count - 1);
                }

                return;
            }
        }
    }

    private async Task RetryAsync()
    {
        var pending = _pendingRetry;
        _pendingRetry = null;
        HasRetry = false;
        if (pending is null) return;
        await pending();
    }

    private void SetStatus(ChatSession session, string text)
    {
        session.Status = text;
        if (session == _current)
        {
            StatusText = text;
        }
    }

    private static bool HasAssistantRowsAfterLastTool(ChatSession session)
    {
        var items = session.Items;
        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (items[i].IsUser) return false;
            if (items[i].IsAssistant) return true;
        }

        return false;
    }

    private async Task SendAsync()
    {
        var session = _current;
        var workspace = string.IsNullOrEmpty(session.Workspace) ? ActiveWorkspace : session.Workspace;
        if (session.IsBusy || string.IsNullOrWhiteSpace(Input) || string.IsNullOrEmpty(workspace)) return;
        var text = Input.Trim();
        _pendingRetry = () => SendCoreAsync(session, workspace, text);
        await SendCoreAsync(session, workspace, text);
    }

    private async Task SendCoreAsync(ChatSession session, string workspace, string text)
    {
        session.BeginTurn();
        var token = session.Cts!.Token;
        Input = "";
        _pendingRetry = null;
        HasRetry = false;
        _logger.LogInformation("Chat send chatId={ChatId} messageLength={Length}", session.ChatId ?? "(new)", text.Length);
        this.RaisePropertyChanged(nameof(IsBusy));

        try
        {
            _turnStartedUtc = DateTime.UtcNow;
            _phase = BusyPhase.Waiting;
            StartElapsedTimer(session);
            SetStatus(session, "Menunggu model… 0s");
            session.Items.Add(new ChatMessageRow("User", text, null));
            ChatMessageRow? live = null;
            var toolCount = 0;
            var pendingTools = new Queue<ChatMessageRow>();

            void AppendDelta(string delta)
            {
                void Apply()
                {
                    if (_phase != BusyPhase.Writing)
                    {
                        _phase = BusyPhase.Writing;
                        StatusText = "Menulis…";
                    }

                    if (live == null)
                    {
                        live = new ChatMessageRow("Assistant", delta, null);
                        session.Items.Add(live);
                    }
                    else
                    {
                        var index = session.Items.IndexOf(live);
                        if (index >= 0)
                        {
                            live = new ChatMessageRow("Assistant", live.Text + delta, null);
                            session.Items[index] = live;
                        }
                    }
                }

                _uiDispatcher(Apply);
            }

            void HandleEvent(ChatStreamEvent ev)
            {
                switch (ev)
                {
                    case StreamStarted started:
                        session.ChatId = started.ChatId;
                        _sessions[started.ChatId] = session;
                        _phase = BusyPhase.Waiting;
                        SetStatus(session, $"Menunggu model… {ElapsedSeconds()}s");
                        break;
                    case StreamDelta delta:
                        AppendDelta(delta.Delta);
                        break;
                    case StreamTool tool:
                        toolCount++;
                        live = null;
                        _phase = BusyPhase.Tool;
                        SetStatus(session, $"Menjalankan tool: {tool.Name}…");
                        var preview = tool.Arguments.Length > 160 ? tool.Arguments[..160] + "..." : tool.Arguments;
                        void AddTool()
                        {
                            var row = new ChatMessageRow("Tool", tool.Name + " " + preview, tool.Name, tool.Arguments);
                            session.Items.Add(row);
                            pendingTools.Enqueue(row);
                            CollapseEarlierToolRows(session, row);
                        }
                        _uiDispatcher(AddTool);
                        break;
                    case StreamToolResult result:
                        _phase = BusyPhase.Waiting;
                        SetStatus(session, $"Menunggu model… {ElapsedSeconds()}s");
                        void ApplyResult()
                        {
                            var args = (string?)null;
                            var index = -1;
                            if (pendingTools.Count > 0)
                            {
                                var row = pendingTools.Dequeue();
                                args = row.Arguments;
                                index = session.Items.IndexOf(row);
                            }
                            var updated = new ChatMessageRow("Tool", result.Output, result.Name, args);
                            updated.IsExpanded = true;
                            if (index >= 0) session.Items[index] = updated;
                            else session.Items.Add(updated);
                            CollapseEarlierToolRows(session, updated);
                        }
                        _uiDispatcher(ApplyResult);
                        break;
                    case StreamDone done:
                    {
                        session.ChatId = done.Turn.ChatId;
                        _sessions[done.Turn.ChatId] = session;
                        var reply = done.Turn.Assistant ?? "";
                        void FinalizeAssistant()
                        {
                            if (reply.Length > 0 && !HasAssistantRowsAfterLastTool(session))
                            {
                                if (live != null)
                                {
                                    var index = session.Items.IndexOf(live);
                                    if (index >= 0)
                                    {
                                        session.Items[index] = new ChatMessageRow("Assistant", reply, null);
                                    }
                                }
                                else
                                {
                                    session.Items.Add(new ChatMessageRow("Assistant", reply, null));
                                }
                            }
                        }
                        _uiDispatcher(FinalizeAssistant);

                        _phase = BusyPhase.Done;
                        SetStatus(session, toolCount > 0 ? $"Selesai — {toolCount} tool call(s)" : "");
                        break;
                    }
                    case StreamFailed failed:
                        _logger.LogError("Chat stream failed: {Message}", failed.Message);
                        _phase = BusyPhase.Done;
                        SetStatus(session, $"Send failed: {failed.Message}");
                        SetRetry(session, workspace, text);
                        break;
                }
            }

            await _apiClient.SendStreamingAsync(workspace, text, session.ChatId, HandleEvent, token);
            _ = RefreshSidebarAsync();
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Chat send cancelled");
            SetStatus(session, "Cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send chat");
            SetStatus(session, $"Send failed: {ex.Message}");
            SetRetry(session, workspace, text);
        }
        finally
        {
            StopElapsedTimer();
            _phase = BusyPhase.Idle;
            session.EndTurn();
            this.RaisePropertyChanged(nameof(IsBusy));
        }
    }

    public void Dispose()
    {
        StopElapsedTimer();
        foreach (var session in _sessions.Values)
        {
            session.CancelTurn();
        }

        _current.CancelTurn();
        LoadCommand.Dispose();
        SendCommand.Dispose();
        RetryCommand.Dispose();
        NewChatCommand.Dispose();
        NewChatInWorkspaceCommand.Dispose();
        PromptDeleteChatCommand.Dispose();
        ConfirmDeleteChatCommand.Dispose();
        CancelDeleteChatCommand.Dispose();
    }

    private bool _isDeletePromptOpen;
    private SidebarRow? _pendingDeleteRow;
    private Func<Task>? _pendingRetry;
    private bool _hasRetry;

    public bool HasRetry
    {
        get => _hasRetry;
        set => this.RaiseAndSetIfChanged(ref _hasRetry, value);
    }

    public bool IsDeletePromptOpen
    {
        get => _isDeletePromptOpen;
        set => this.RaiseAndSetIfChanged(ref _isDeletePromptOpen, value);
    }

    public string DeletePromptText => _pendingDeleteRow is null
        ? ""
        : $"Delete chat “{_pendingDeleteRow.DisplayName}”? This can’t be undone.";

    private void PromptDeleteChat(SidebarRow? row)
    {
        if (row is null || !row.IsChat || row.ChatId is null)
        {
            return;
        }

        _pendingDeleteRow = row;
        IsDeletePromptOpen = true;
        this.RaisePropertyChanged(nameof(DeletePromptText));
    }

    private async Task ConfirmDeleteChatAsync()
    {
        var row = _pendingDeleteRow;
        _pendingDeleteRow = null;
        IsDeletePromptOpen = false;
        if (row?.ChatId is null)
        {
            return;
        }

        var ok = await _apiClient.DeleteChatAsync(row.ChatId);
        if (!ok)
        {
            SetStatus(_current, "Chat delete failed — the backend probably does not have this endpoint yet (deploy needed).");
            return;
        }

        _sessions.Remove(row.ChatId);
        if (_current.ChatId == row.ChatId)
        {
            StartChatIn(ActiveWorkspace);
        }

        await RefreshSidebarAsync();
        SetStatus(_current, "Chat deleted.");
    }

    private int ElapsedSeconds() => (int)(DateTime.UtcNow - _turnStartedUtc).TotalSeconds;

    private static void CollapseEarlierToolRows(ChatSession session, ChatMessageRow keep)
    {
        foreach (var item in session.Items)
        {
            if (item.IsTool && !ReferenceEquals(item, keep) && item.IsExpanded)
            {
                item.IsExpanded = false;
            }
        }
    }

    private void StartElapsedTimer(ChatSession session)
    {
        StopElapsedTimer();
        _elapsedTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) =>
        {
            if (_phase == BusyPhase.Waiting && ReferenceEquals(session, _current))
            {
                StatusText = $"Menunggu model… {ElapsedSeconds()}s";
            }
        };
        _elapsedTimer.Start();
    }

    private void StopElapsedTimer()
    {
        _elapsedTimer?.Stop();
        _elapsedTimer = null;
    }
}
