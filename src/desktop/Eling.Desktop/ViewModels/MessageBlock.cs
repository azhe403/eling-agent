using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Input.Platform;

namespace Eling.Desktop.ViewModels;

public enum MessageBlockType
{
    Text,
    Code
}

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

public static class SimpleMarkdownParser
{
    private static readonly Regex CodeFenceRegex = new(@"\x60\x60\x60([a-zA-Z0-9_-]*)\r?\n([\s\S]*?)\x60\x60\x60", RegexOptions.Compiled);

    public static IReadOnlyList<MessageBlock> Parse(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return [];
        }

        var blocks = new List<MessageBlock>();
        var lastIndex = 0;

        var matches = CodeFenceRegex.Matches(input);
        foreach (Match match in matches)
        {
            if (match.Index > lastIndex)
            {
                var textChunk = input[lastIndex..match.Index].Trim('\r', '\n');
                if (!string.IsNullOrEmpty(textChunk))
                {
                    blocks.Add(new MessageBlock(MessageBlockType.Text, textChunk));
                }
            }

            var lang = match.Groups[1].Value.Trim();
            var code = match.Groups[2].Value.TrimEnd('\r', '\n');
            blocks.Add(new MessageBlock(MessageBlockType.Code, code, lang));

            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < input.Length)
        {
            var remaining = input[lastIndex..].Trim('\r', '\n');
            if (!string.IsNullOrEmpty(remaining))
            {
                blocks.Add(new MessageBlock(MessageBlockType.Text, remaining));
            }
        }

        return blocks;
    }
}
