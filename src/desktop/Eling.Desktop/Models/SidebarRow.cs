namespace Eling.Desktop.Models;

public class SidebarRow
{
    public SidebarRow(
        bool isFolder,
        string displayName,
        string workspace,
        string? chatId = null,
        ChatSummaryDto? chat = null,
        bool isMoreRow = false)
    {
        IsFolder = isFolder;
        IsMoreRow = isMoreRow;
        DisplayName = displayName;
        Workspace = workspace;
        ChatId = chatId;
        Chat = chat;
    }

    public bool IsFolder { get; }
    public bool IsMoreRow { get; }
    public bool IsChat => !IsFolder && !IsMoreRow;
    public string DisplayName { get; }
    public string Workspace { get; }
    public string? ChatId { get; }
    public ChatSummaryDto? Chat { get; }

    public string Icon => IsFolder ? "📁" : IsMoreRow ? "➕" : "💬";
    public string ItemDisplay => IsMoreRow ? DisplayName : $"{Icon} {DisplayName}";
    public double FontSize => IsFolder ? 12.0 : 11.5;
    public string FontWeight => IsFolder ? "SemiBold" : "Normal";
    public string TextColor => IsFolder ? "#e5e7eb" : IsMoreRow ? "#60a5fa" : "#9ca3af";
    public string BackgroundColor => IsFolder ? "#1e1e24" : "Transparent";
}
