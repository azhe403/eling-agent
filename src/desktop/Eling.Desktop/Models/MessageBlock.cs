using Avalonia;
using Avalonia.Input.Platform;

namespace Eling.Desktop.Models;

/// <summary>
/// One rendered piece of a chat message: either prose or a fenced code block.
/// Produced by the formatters, bound by the view — it carries no behaviour beyond
/// deriving what the row displays and copying its own content to the clipboard.
/// </summary>
public sealed class MessageBlock
{
    public MessageBlockType Type { get; }
    public string Content { get; }
    public string? Language { get; }

    public bool IsCode => Type == MessageBlockType.Code;
    public bool IsText => Type == MessageBlockType.Text;
    public string LanguageDisplay => string.IsNullOrWhiteSpace(Language) ? "code" : Language.ToLowerInvariant();

    public MessageBlock(MessageBlockType type, string content, string? language = null)
    {
        Type = type;
        Content = content;
        Language = language;
    }

    public async void CopyToClipboard()
    {
        try
        {
            if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop &&
                desktop.MainWindow?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(Content);
            }
        }
        catch
        {
        }
    }
}
