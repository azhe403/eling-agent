using System;
using System.Collections.Generic;

namespace Eling.Desktop.Models;

public record MemoryDto(
    string Id,
    string Type,
    string Status,
    string Content,
    List<string>? Tags,
    string? Source,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? Scope = null,
    ProjectInfoDto? Project = null)
{
    public bool IsGlobalScope => string.Equals(Scope, "global", StringComparison.OrdinalIgnoreCase);
    public bool IsProjectScope => !IsGlobalScope;

    public string ScopeDisplay => IsGlobalScope
        ? "🌐 Global"
        : (Project != null && !string.IsNullOrWhiteSpace(Project.Id) ? $"📁 {Project.Id}" : "📁 Project");
}

/// <summary>
/// The memory list envelope: the requested window's <see cref="Items"/> plus
/// <see cref="Metadata"/> carrying the full count the query matches before
/// paging, so the UI can say how many exist without guessing from the page.
/// </summary>
public record MemoryListDto(
    MemoryListMetadata Metadata,
    List<MemoryDto> Items);

/// <summary>Paging metadata for a memory list: the scope size, how many survive the request's filters, and the window asked for.</summary>
public record MemoryListMetadata(
    int Total,
    int Matching,
    int? Limit,
    int? Offset);
