using Avalonia;
using Avalonia.Input.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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

    private readonly ILogger<MessageBlock>? _logger;

    public MessageBlock(MessageBlockType type, string content, string? language = null, ILogger<MessageBlock>? logger = null)
    {
        Type = type;
        Content = content;
        Language = language;
        _logger = logger;
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
        catch (Exception ex)
        {
            // Clipboard failure is benign; copy is fire-and-forget.
            // Constructor-injected when possible; otherwise resolved from the
            // app's service container like the view code does to reach view models.
            if (_logger is not null)
                _logger.LogDebug(ex, "Clipboard copy failed");
            else if (Application.Current is App app && app.Services is not null)
                app.Services.GetService<ILogger<MessageBlock>>()?.LogDebug(ex, "Clipboard copy failed");
        }
    }
}
