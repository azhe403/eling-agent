namespace Eling.Backend.Dtos;

public sealed record ProjectStatusDto(
    string Cwd,
    bool IsUserHome,
    bool Initialized,
    bool HasOwnScope,
    string? HeadRoot,
    bool Adoptable,
    IReadOnlyCollection<string> AncestorScopes,
    string Posture,
    string Policy,
    string PolicyLevel,
    DateTimeOffset? PolicyEntryCreatedAt,
    DateTimeOffset? PolicyEntryUpdatedAt,
    DateTimeOffset? PolicyDocumentCreatedAt,
    DateTimeOffset? PolicyDocumentUpdatedAt);
