using System.Text.Json.Serialization;
using Eling.Core;

namespace Eling.Mcp;

public sealed class SaveMemoryResponse
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = "";

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
    public string? Scope { get; set; }

    public static SaveMemoryResponse From(SaveResult result, string? scope = null) => new()
    {
        Action = result.Action == SaveAction.Created ? "created" : "updated",
        Id = result.Memory.Id,
        Type = result.Memory.Type,
        Status = result.Memory.Status,
        Content = result.Memory.Content,
        Tags = result.Memory.Tags,
        CreatedAt = result.Memory.CreatedAt,
        UpdatedAt = result.Memory.UpdatedAt,
        Source = result.Memory.Source,
        Scope = scope
    };

    public static SaveMemoryResponse From(ScopedSaveResult result) => new()
    {
        Action = result.Action == SaveAction.Created ? "created" : "updated",
        Id = result.Memory.Id,
        Type = result.Memory.Type,
        Status = result.Memory.Status,
        Content = result.Memory.Content,
        Tags = result.Memory.Tags,
        CreatedAt = result.Memory.CreatedAt,
        UpdatedAt = result.Memory.UpdatedAt,
        Source = result.Memory.Source,
        Scope = result.Scope == MemoryScopeKind.Global ? "global" : "project"
    };
}
