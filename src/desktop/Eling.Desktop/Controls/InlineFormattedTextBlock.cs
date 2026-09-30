using System;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Eling.Desktop.Controls;

/// <summary>
/// Renders inline markdown inside a text block: <c>`code`</c>, <c>**bold**</c>
/// and <c>*italic*</c>. The block-level parser only handles fenced blocks, so
/// without this an assistant line like <c>CWD: `C:\some\path`</c> shows its
/// backticks verbatim.
/// </summary>
/// <remarks>
/// Flat by construction: <see cref="Rebuild"/> makes one pass and never recurses,
/// so an emphasised span cannot contain a styled span. Two consequences to know
/// about before trusting it with arbitrary model output — a single <c>*</c> on
/// each side of ordinary text renders as italic (<c>2 * 3 * 4</c> loses its
/// asterisks), and <c>**bold with *italic* inside**</c> leaves a stray <c>*</c>
/// because the content pattern stops at the first inner asterisk. Fixing this
/// means either recursing per span or taking a markdown dependency.
/// </remarks>
public sealed class InlineFormattedTextBlock : TextBlock
{
    private static readonly Regex TokenRegex = new(
        "(`[^`]+`|\\*\\*[^*]+\\*\\*|\\*[^*]+\\*)",
        RegexOptions.Compiled);

    public static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<InlineFormattedTextBlock, string?>(nameof(Source));

    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    static InlineFormattedTextBlock()
    {
        SourceProperty.Changed.AddClassHandler<InlineFormattedTextBlock>((block, _) => block.Rebuild());
    }

    private void Rebuild()
    {
        Inlines.Clear();

        var source = Source;
        if (string.IsNullOrEmpty(source))
        {
            return;
        }

        var cursor = 0;
        foreach (Match match in TokenRegex.Matches(source))
        {
            if (match.Index > cursor)
            {
                Inlines.Add(new Run(source[cursor..match.Index]));
            }

            var token = match.Value;
            if (token.StartsWith('`') && token.EndsWith('`') && token.Length > 2)
            {
                Inlines.Add(BuildCode(token[1..^1]));
            }
            else if (token.StartsWith("**", StringComparison.Ordinal) && token.Length > 4)
            {
                Inlines.Add(BuildEmphasised(token[2..^2], bold: true));
            }
            else if (token.StartsWith('*') && token.EndsWith('*') && token.Length > 2)
            {
                Inlines.Add(BuildEmphasised(token[1..^1], bold: false));
            }
            else
            {
                Inlines.Add(new Run(token));
            }

            cursor = match.Index + match.Length;
        }

        if (cursor < source.Length)
        {
            Inlines.Add(new Run(source[cursor..]));
        }
    }

    private static InlineUIContainer BuildCode(string value)
    {
        var border = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#1f2430")),
            CornerRadius = new CornerRadius(3),
            Padding = new Avalonia.Thickness(3, 0, 3, 0),
            Child = new TextBlock
            {
                Text = value,
                FontFamily = new FontFamily("Cascadia Code,Consolas,Menlo,monospace"),
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#a5d6a7"))
            }
        };

        return new InlineUIContainer(border);
    }

    private static Run BuildEmphasised(string value, bool bold) => new(value)
    {
        FontWeight = bold ? Avalonia.Media.FontWeight.SemiBold : FontWeight.Normal,
        FontStyle = bold ? FontStyle.Normal : FontStyle.Italic
    };
}
