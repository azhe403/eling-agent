using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Eling.Desktop.ViewModels;

/// One node of a navigable JSON tree. Children are themselves nodes, so the view
/// can render the structure to any depth; each node carries its own expand state
/// because a collapsed branch must not reopen when an unrelated sibling expands.
public sealed class JsonTreeNode : INotifyPropertyChanged
{
    private bool _isExpanded;

    public string Label { get; }
    public string? Value { get; }
    public IReadOnlyList<JsonTreeNode> Children { get; }

    public bool IsLeaf => Children.Count == 0;
    public bool IsExpanded => _isExpanded;
    public string Chevron => IsLeaf ? string.Empty : _isExpanded ? "▾" : "▸";
    public string Display => Value is null ? Label : $"{Label}: {Value}";

    public event PropertyChangedEventHandler? PropertyChanged;

    public JsonTreeNode(
        string label,
        string? value,
        IReadOnlyList<JsonTreeNode> children,
        bool alwaysExpanded = false)
    {
        Label = label;
        Value = value;
        Children = children;

        // The row is height-capped by a scroll viewer, so opening the first level
        // costs no layout and saves a click on every turn. Deeper branches still
        // start collapsed when wide, or the tree becomes an unreadable wall.
        _isExpanded = children.Count > 0 && (alwaysExpanded || children.Count <= 12);
    }

    public void Toggle()
    {
        _isExpanded = !_isExpanded;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Chevron)));
    }

    public static IReadOnlyList<JsonTreeNode> Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return [Build("(root)", doc.RootElement, 0)];
        }
        catch (JsonException)
        {
            // Not valid JSON. Keep the row usable instead of hiding the view:
            // one leaf holding the raw text, so the mode toggle still appears and
            // the code view can show exactly what the tool returned.
            return string.IsNullOrWhiteSpace(json)
                ? []
                : [new JsonTreeNode("(raw)", json, [])];
        }
    }

    private static JsonTreeNode Build(string label, JsonElement element, int depth)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var children = new List<JsonTreeNode>();
                foreach (var property in element.EnumerateObject())
                {
                    children.Add(Build(property.Name, property.Value, depth + 1));
                }

                return new JsonTreeNode(label, null, children, alwaysExpanded: depth <= 1);
            }

            case JsonValueKind.Array:
            {
                var children = new List<JsonTreeNode>();
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    children.Add(Build($"[{index}]", item, depth + 1));
                    index++;
                }

                return new JsonTreeNode($"{label} ({children.Count})", null, children, alwaysExpanded: depth <= 1);
            }

            default:
                return new JsonTreeNode(label, Scalar(element), []);
        }
    }

    private static string Scalar(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => $"\"{element.GetString()}\"",
        JsonValueKind.Null => "null",
        JsonValueKind.True or JsonValueKind.False => element.GetBoolean().ToString(),
        _ => element.GetRawText()
    };
}
