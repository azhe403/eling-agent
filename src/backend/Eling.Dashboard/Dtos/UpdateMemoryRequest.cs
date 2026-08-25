namespace Eling.Dashboard.Dtos;

public record UpdateMemoryRequest(
    string? Content,
    string? Type,
    List<string>? Tags,
    string? Source,
    string? Status);
