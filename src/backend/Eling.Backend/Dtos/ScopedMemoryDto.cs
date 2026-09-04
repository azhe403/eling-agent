using Eling.Core;

namespace Eling.Backend.Dtos;

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