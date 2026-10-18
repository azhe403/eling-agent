using Eling.Core.Database;

namespace Eling.Backend.Dtos;

public sealed record DatabaseConnectionView(
    string Id,
    string Name,
    DatabaseProvider Provider,
    DatabaseEnvironment Environment,
    bool HasConnectionString,
    bool AllowAgentMutations,
    bool RequireApprovalForMutations,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastConnectedAt);

public sealed record CreateDatabaseConnectionRequest(
    string Name,
    DatabaseProvider Provider,
    string ConnectionString,
    DatabaseEnvironment Environment,
    bool AllowAgentMutations,
    bool RequireApprovalForMutations);

public sealed record UpdateDatabaseConnectionRequest(
    string? Name,
    DatabaseEnvironment? Environment,
    string? ConnectionString,
    bool? AllowAgentMutations,
    bool? RequireApprovalForMutations);

public sealed record DatabaseTestResponse(
    bool Ok,
    string Message,
    long LatencyMs);

public sealed record DatabaseQueryRequest(
    string Sql,
    int MaxRows = 100);

public sealed record DetectedConnectionDto(
    string SettingsKey,
    string Name,
    string MaskedConnectionString,
    DatabaseProvider? Provider,
    string? RejectionReason);

public sealed record ImportRequest(string[] SettingsKeys);

public sealed record ImportResultDto(
    string SettingsKey,
    string? ConnectionId,
    string? Name,
    bool Imported,
    string? Reason);
