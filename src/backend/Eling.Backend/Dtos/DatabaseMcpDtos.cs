using Eling.Core.Database;

namespace Eling.Backend.Dtos;

public sealed record McpDetectedCandidate(
    int Index,
    string SettingsKey,
    string Name,
    DatabaseProvider? Provider,
    string? RejectionReason);
