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
    DateTimeOffset? LastConnectedAt,
    DatabaseConnectionDetails? Details = null,
    bool IsServerLevel = false);

/// <summary>
/// The stored connection read back into fields, so the dashboard can pre-fill
/// its form. It carries no password: the credential never leaves the backend.
/// </summary>
public sealed record DatabaseConnectionDetails(
    string? Host,
    int? Port,
    string? Database,
    string? Username,
    string? FilePath);

public sealed record CreateDatabaseConnectionRequest(
    string Name,
    DatabaseProvider Provider,
    string? ConnectionString,
    DatabaseEnvironment Environment,
    bool AllowAgentMutations,
    bool RequireApprovalForMutations,
    ManualConnectionFields? Manual = null);

public sealed record UpdateDatabaseConnectionRequest(
    string? Name,
    DatabaseEnvironment? Environment,
    string? ConnectionString,
    bool? AllowAgentMutations,
    bool? RequireApprovalForMutations,
    ManualConnectionFields? Manual = null);

/// <summary>
/// A connection to try out before it is saved. Both shapes are optional: with
/// neither, the stored connection is tested as it is.
/// </summary>
public sealed record TestDatabaseConnectionRequest(
    string? ConnectionString = null,
    ManualConnectionFields? Manual = null);

public sealed record DatabaseTestResponse(
    bool Ok,
    string Message,
    long LatencyMs);

public sealed record DatabaseQueryRequest(
    string Sql,
    int MaxRows = 100,
    string? Database = null);

public sealed record DatabaseMutateRequest(
    string Sql,
    int MaxAffectedRows = 1000,
    string? Database = null);

public sealed record DatabaseMutateResponse(
    int AffectedRows,
    bool Truncated);

public sealed record DetectedConnectionDto(
    string SettingsKey,
    string Name,
    string MaskedConnectionString,
    DatabaseProvider? Provider,
    string? RejectionReason,
    string? SourceFile = null);

public sealed record ImportRequest(string[] SettingsKeys);

public sealed record ImportResultDto(
    string SettingsKey,
    string? ConnectionId,
    string? Name,
    bool Imported,
    string? Reason);
