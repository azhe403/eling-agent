using System.Text.Json.Serialization;
using Eling.Core;

namespace Eling.Backend.Dtos;

/// <summary>
/// Payload returned by the <c>memory_recall</c> tool. Bundles the most
/// relevant full memories for the given topics, the most recently updated
/// active memories, and outstanding intentions so a client can hydrate
/// context on demand at any point in a conversation.
/// </summary>
public sealed class MemoryRecallMemory
{
    private const int ContentMaxLength = 200;

    [JsonPropertyName("id")]
    public MemoryId Id { get; set; }

    [JsonPropertyName("type")]
    public MemoryType Type { get; set; }

    [JsonPropertyName("status")]
    public MemoryStatus Status { get; set; }

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    [JsonPropertyName("tags")]
    public IReadOnlyCollection<string> Tags { get; set; } = [];

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "project";

    public static MemoryRecallMemory From(Memory memory) => new()
    {
        Id = memory.Id,
        Type = memory.Type,
        Status = memory.Status,
        Content = Truncate(memory.Content, ContentMaxLength),
        Tags = memory.Tags,
        CreatedAt = memory.CreatedAt,
        UpdatedAt = memory.UpdatedAt,
        Source = memory.Source,
        Scope = "project"
    };

    public static MemoryRecallMemory From(ScopedMemory scoped) => new()
    {
        Id = scoped.Memory.Id,
        Type = scoped.Memory.Type,
        Status = scoped.Memory.Status,
        Content = Truncate(scoped.Memory.Content, ContentMaxLength),
        Tags = scoped.Memory.Tags,
        CreatedAt = scoped.Memory.CreatedAt,
        UpdatedAt = scoped.Memory.UpdatedAt,
        Source = scoped.Memory.Source,
        Scope = scoped.Scope == MemoryScopeKind.Global ? "global" : "project"
    };

    private static string Truncate(string content, int maxLength)
    {
        if (string.IsNullOrEmpty(content) || content.Length <= maxLength)
        {
            return content;
        }
        return content.Substring(0, maxLength) + "...";
    }
}
