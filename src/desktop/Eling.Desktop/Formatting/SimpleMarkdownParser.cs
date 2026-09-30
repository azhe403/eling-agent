using System.Text.RegularExpressions;
using Eling.Desktop.Models;

namespace Eling.Desktop.Formatting;

/// <summary>
/// Splits a markdown message into the text and fenced-code blocks a chat row
/// renders. Deliberately fence-only: inline spans are handled by the view's
/// <c>InlineFormattedTextBlock</c>, so this stays a pure function of the input.
/// </summary>
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
