namespace Eling.Backend.Dtos;

public record ScopedSearchResultDto(string Id, double Rank, string Scope, string? ProjectRoot);