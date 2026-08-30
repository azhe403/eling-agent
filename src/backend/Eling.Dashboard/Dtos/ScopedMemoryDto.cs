using Eling.Core;

namespace Eling.Dashboard.Dtos;

public record ScopedMemoryDto(
    string Id,
    string Type,
    string Status,
    string Content,
    List<string> Tags,
    string? Source,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Scope,
    ProjectInfoDto? Project)
{
    public static ScopedMemoryDto From(Memory memory, MemoryScopeKind scope, string? projectRoot)
    {
        return new ScopedMemoryDto(
            memory.Id.Value,
            memory.Type.ToString(),
            memory.Status.ToString(),
            memory.Content,
            memory.Tags.ToList(),
            memory.Source,
            memory.CreatedAt,
            memory.UpdatedAt,
            scope.ToString().ToLowerInvariant(),
            scope == MemoryScopeKind.Project && projectRoot is not null
                ? new ProjectInfoDto(Path.GetFileName(projectRoot.TrimEnd(Path.DirectorySeparatorChar)), projectRoot)
                : null);
    }
}

public record ProjectInfoDto(string Id, string Root);

public record ScopedSearchResultDto(string Id, double Rank, string Scope, string? ProjectRoot);

public record CopyRequest(
    string Id,
    string SourceScope,
    string? SourceProjectRoot,
    string TargetProjectRoot);

public record PromoteRequest(
    string Id,
    string SourceProjectRoot,
    bool Move = false);
