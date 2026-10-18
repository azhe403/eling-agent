namespace Eling.Core.Database;

public sealed record DetectedConnection(
    string SettingsKey,
    string Name,
    string ConnectionString,
    string MaskedConnectionString,
    DatabaseProvider? Provider,
    string? RejectionReason);
