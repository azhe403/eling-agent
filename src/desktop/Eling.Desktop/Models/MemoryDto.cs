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
