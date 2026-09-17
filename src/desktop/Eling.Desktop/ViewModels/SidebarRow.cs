using Eling.Desktop.Models;

namespace Eling.Desktop.ViewModels;

public class SidebarRow
{
    public SidebarRow(
        bool isFolder,
        string displayName,
        string workspace,
        string? chatId = null,
        ChatSummaryDto? chat = null)
    {
        IsFolder = isFolder;
        DisplayName = displayName;
        Workspace = workspace;
        ChatId = chatId;
        Chat = chat;
    }

    public bool IsFolder { get; }
    public bool IsChat => !IsFolder;
    public string DisplayName { get; }
    public string Workspace { get; }
    public string? ChatId { get; }
    public ChatSummaryDto? Chat { get; }
}
