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
    private readonly Dictionary<string, ChatSession> _sessions = [];
    private ChatSession _current;

    private string _activeWorkspace = "";
    private string _input = "";
    private string _statusText = "";
    private SidebarRow? _selectedRow;
    private string _selectedModel = "";

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
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> NewChatCommand { get; }
    public ReactiveCommand<string, System.Reactive.Unit> NewChatInWorkspaceCommand { get; }

    public ChatViewModel(
        ElingApiClient apiClient,
        ILogger<ChatViewModel> logger)
    {
        _apiClient = apiClient;
        _logger = logger;
        _current = new ChatSession("", null);

        LoadCommand = ReactiveCommand.CreateFromTask(() => LoadAsync());
        SendCommand = ReactiveCommand.CreateFromTask(() => SendAsync());
        NewChatCommand = ReactiveCommand.Create(() =>
        {
            StartChatIn(ActiveWorkspace);
        });
        NewChatInWorkspaceCommand = ReactiveCommand.Create<string>(workspace =>
        {
            StartChatIn(workspace);
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
                SidebarItems.Add(new SidebarRow(true, FolderNameOf(root), root));
                IReadOnlyList<ChatSummaryDto> chats = [];
                try
                {
                    chats = await _apiClient.GetChatsAsync(root);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load chats for {Workspace}", root);
                }

                foreach (var chat in chats)
                {
                    SidebarItems.Add(new SidebarRow(false, chat.Title, root, chat.Id, chat));
                }
            }

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
                    session.Items.Add(new ChatMessageRow(message.Role, message.Text, message.ToolName, startsExpanded: false));
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
        session.BeginTurn();
        var token = session.Cts!.Token;
        var text = Input.Trim();
        Input = "";
        _logger.LogInformation("Chat send chatId={ChatId} messageLength={Length}", session.ChatId ?? "(new)", text.Length);
        this.RaisePropertyChanged(nameof(IsBusy));

        try
        {
            session.Items.Add(new ChatMessageRow("User", text, null));
            ChatMessageRow? live = null;
            var toolCount = 0;
            var pendingTools = new Queue<ChatMessageRow>();

            void AppendDelta(string delta)
            {
                void Apply()
                {
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

                if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                {
                    Apply();
                }
                else
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(Apply);
                }
            }

            void HandleEvent(ChatStreamEvent ev)
            {
                switch (ev)
                {
                    case StreamStarted started:
                        session.ChatId = started.ChatId;
                        _sessions[started.ChatId] = session;
                        SetStatus(session, "");
                        break;
                    case StreamDelta delta:
                        AppendDelta(delta.Delta);
                        break;
                    case StreamTool tool:
                        toolCount++;
                        live = null;
                        var preview = tool.Arguments.Length > 160 ? tool.Arguments[..160] + "..." : tool.Arguments;
                        void AddTool()
                        {
                            var row = new ChatMessageRow("Tool", tool.Name + " " + preview, tool.Name, tool.Arguments);
                            session.Items.Add(row);
                            pendingTools.Enqueue(row);
                        }
                        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) AddTool();
                        else Avalonia.Threading.Dispatcher.UIThread.Post(AddTool);
                        break;
                    case StreamToolResult result:
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
                        }
                        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) ApplyResult();
                        else Avalonia.Threading.Dispatcher.UIThread.Post(ApplyResult);
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
                        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) FinalizeAssistant();
                        else Avalonia.Threading.Dispatcher.UIThread.Post(FinalizeAssistant);

                        SetStatus(session, toolCount > 0 ? $"Completed with {toolCount} tool call(s)" : "");
                        break;
                    }
                    case StreamFailed failed:
                        _logger.LogError("Chat stream failed: {Message}", failed.Message);
                        SetStatus(session, failed.Message);
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
        }
        finally
        {
            session.EndTurn();
            this.RaisePropertyChanged(nameof(IsBusy));
        }
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
        {
            session.CancelTurn();
        }

        _current.CancelTurn();
        LoadCommand.Dispose();
        SendCommand.Dispose();
        NewChatCommand.Dispose();
        NewChatInWorkspaceCommand.Dispose();
    }
}
