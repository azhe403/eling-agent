using System.Text.Json.Serialization;
using Eling.Core;
using Eling.Core.Memory;

namespace Eling.Backend.Dtos;

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
    [JsonPropertyName("previousContent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PreviousContent { get; set; }
    [JsonPropertyName("previousTags")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyCollection<string>? PreviousTags { get; set; }
    [JsonPropertyName("nearMatches")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyCollection<NearMatchDto>? NearMatches { get; set; }
    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; set; }
    [JsonPropertyName("matchScore")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MatchScore { get; set; }
    [JsonPropertyName("projectName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProjectName { get; set; }
    [JsonPropertyName("projectRoot")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProjectRoot { get; set; }
    [JsonPropertyName("initRequired")]
    public bool InitRequired { get; set; }
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    /// <summary>True when a project-targeted save was routed to global because the workspace's policy is <c>disabled</c>.</summary>
    [JsonPropertyName("projectScopeDisabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ProjectScopeDisabled { get; set; }

    /// <summary>Human-readable explanation accompanying a rerouted save or other special outcome.</summary>
    [JsonPropertyName("note")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; set; }

    public static SaveMemoryResponse FromInitRequired(string cwd) => new()
    {
        Action = "init-required",
        Scope = "project",
        InitRequired = true,
        Message = $"No project scope initialized at '{cwd}'. Ask the user for approval, then call memory_init_project."
    };


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
        Scope = scope,
        PreviousContent = result.Previous?.Content,
        PreviousTags = result.Previous?.Tags.ToList(),
        NearMatches = result.NearMatches.Count > 0 ? result.NearMatches.Select(NearMatchDto.From).ToList() : null,
        Reason = result.Reason,
        MatchScore = result.MatchScore
    };

    public static SaveMemoryResponse From(ScopedSaveResult result, bool projectScopeDisabled = false, string? note = null) => new()
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
        Scope = result.Scope == MemoryScopeKind.Global ? "global" : "project",
        PreviousContent = result.Previous?.Memory.Content,
        PreviousTags = result.Previous?.Memory.Tags.ToList(),
        NearMatches = result.NearMatches.Count > 0 ? result.NearMatches.Select(NearMatchDto.From).ToList() : null,
        Reason = result.Reason,
        MatchScore = result.MatchScore,
        ProjectName = result.ProjectName,
        ProjectRoot = result.ProjectRoot,
        ProjectScopeDisabled = projectScopeDisabled,
        Note = note
    };
}

public sealed class NearMatchDto
{
    [JsonPropertyName("id")]
    public MemoryId Id { get; set; }

    [JsonPropertyName("contentPreview")]
    public string ContentPreview { get; set; } = "";

    [JsonPropertyName("score")]
    public double Score { get; set; }

    [JsonPropertyName("type")]
    public MemoryType Type { get; set; }

    [JsonPropertyName("tags")]
    public IReadOnlyCollection<string> Tags { get; set; } = [];

    public static NearMatchDto From(NearMatch match) => new()
    {
        Id = match.Id,
        ContentPreview = match.ContentPreview,
        Score = match.Score,
        Type = match.Type,
        Tags = match.Tags
    };
}

