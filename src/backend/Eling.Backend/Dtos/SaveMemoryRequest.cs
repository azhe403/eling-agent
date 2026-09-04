namespace Eling.Backend.Dtos;

public record SaveMemoryRequest(
    string? Type,
    string Content,
    List<string>? Tags,
    string? Source);