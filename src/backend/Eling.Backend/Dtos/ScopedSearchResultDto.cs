using System.Text.Json.Serialization;
using Eling.Core;
using Eling.Core.Memory;

namespace Eling.Backend.Dtos;

public record ScopedSearchResultDto(
    string Id,
    double Rank,
    string Scope,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProjectName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProjectRoot)
{
    private static string? NameOf(string? root)
        => root is null ? null : Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar));

    public static ScopedSearchResultDto From(ScopedSearchResult result)
        => new(
            result.Id.Value,
            result.Rank,
            result.Scope == MemoryScopeKind.Global ? "global" : "project",
            NameOf(result.ProjectRoot),
            result.ProjectRoot);

    public static ScopedSearchResultDto Global(MemorySearchResult result)
        => new(result.Id.Value, result.Rank, "global", null, null);

    public static ScopedSearchResultDto Project(MemorySearchResult result, string projectRoot)
        => new(result.Id.Value, result.Rank, "project", NameOf(projectRoot), projectRoot);
}