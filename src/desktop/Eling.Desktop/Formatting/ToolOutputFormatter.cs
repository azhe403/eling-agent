using System;
using System.Collections.Generic;
using System.Text.Json;
using Eling.Desktop.Models;

namespace Eling.Desktop.Formatting;

/// <summary>
/// Prepares tool output for display in a chat row.
/// </summary>
/// <para>
/// Tool payloads are always JSON, so there is exactly one renderer: a navigable
/// tree (see <c>JsonTreeNode</c>) and a pretty-printed code view. Only the tool
/// ARGUMENTS need bespoke handling, because those arrive as a raw JSON string on
/// the header line rather than as a payload.
/// </para>
public static class ToolOutputFormatter
{
    private const int MaxArgumentValueLength = 60;
    private const string ErrorPrefix = "error:";

    // The backend serialises tool JSON compact because that is what the model
    // reads back in history and minified costs fewer tokens. Only the transcript
    // view is beautified, never the payload itself.
    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true
    };

    public static bool IsError(string? output) =>
        output is not null && output.TrimStart().StartsWith(ErrorPrefix, StringComparison.OrdinalIgnoreCase);

    public static string FormatArguments(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Truncate(argumentsJson.Trim(), MaxArgumentValueLength);
            }

            var parts = new List<string>();
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                parts.Add($"{property.Name} = {Scalar(property.Value)}");
            }

            return parts.Count == 0
                ? Truncate(argumentsJson.Trim(), MaxArgumentValueLength)
                : string.Join(", ", parts);
        }
        catch (JsonException)
        {
            return Truncate(argumentsJson.Trim(), MaxArgumentValueLength);
        }
    }

    /// The raw view: the exact payload, pretty-printed and monospaced, for copying
    /// or checking exactly what the tool returned. Non-JSON output is shown as text.
    public static IReadOnlyList<MessageBlock> BuildRawBlocks(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        if (IsError(output))
        {
            return [new MessageBlock(MessageBlockType.Text, output)];
        }

        return IsJsonPayload(output)
            ? [new MessageBlock(MessageBlockType.Code, PrettyJson(output), "json")]
            : [new MessageBlock(MessageBlockType.Text, output)];
    }

    private static bool IsJsonPayload(string output)
    {
        var trimmed = output.TrimStart();
        if (!trimmed.StartsWith('{') && !trimmed.StartsWith('['))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(output);
            return doc.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string PrettyJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, PrettyJsonOptions);
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static string Scalar(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.Null => "null",
        JsonValueKind.True or JsonValueKind.False => element.GetBoolean().ToString(),
        JsonValueKind.Number => element.GetRawText(),
        _ => Truncate(element.GetRawText(), MaxArgumentValueLength)
    };

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "\u2026";
}
