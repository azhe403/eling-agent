using Eling.Core.Database;

namespace Eling.Backend.Dtos;

public sealed record McpDetectedCandidate(
    int Index,
    string SettingsKey,
    string Name,
    DatabaseProvider? Provider,
    string? RejectionReason,
    string? SourceFile = null);

public sealed record McpConnectionsOverview(
    IReadOnlyList<DatabaseConnectionView> Registered,
    IReadOnlyList<McpDetectedCandidate> Unimported);
