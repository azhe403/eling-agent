using System.Collections.ObjectModel;
using System.Threading;

namespace Eling.Desktop.Models;

/// <summary>
/// One open conversation in a workspace. Owns the rows it displays and the
/// cancellation source for the turn currently running in it.
/// </summary>
public sealed class ChatSession
{
    public ChatSession(string workspace, string? chatId)
    {
        Workspace = workspace;
        ChatId = chatId;
        HistoryRequested = chatId is null;
    }

    public string Workspace { get; }
    public string? ChatId { get; set; }
    public ObservableCollection<ChatMessageRow> Items { get; } = [];
    public bool HistoryRequested { get; set; }
    public string Status { get; set; } = "";
    public CancellationTokenSource? Cts { get; private set; }
    public bool IsBusy => Cts is not null;

    public void BeginTurn()
    {
        Cts?.Cancel();
        Cts?.Dispose();
        Cts = new CancellationTokenSource();
    }

    public void EndTurn()
    {
        Cts?.Dispose();
        Cts = null;
    }

    public void CancelTurn()
    {
        Cts?.Cancel();
        EndTurn();
    }
}
