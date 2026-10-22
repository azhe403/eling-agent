namespace Eling.Core.Database;

/// <summary>
/// A connection in the shape a person types it into the dashboard: a host, a
/// port, a database and credentials - or a file path for SQLite.
///
/// The password lives here only on the way in. It is never persisted in this
/// record and never leaves the backend: the API answers with
/// <see cref="DatabaseConnectionDetails"/>-style values that carry no
/// password at all.
/// </summary>
public sealed record ManualConnectionFields(
    string? Host,
    int? Port,
    string? Database,
    string? Username,
    string? Password,
    string? FilePath);
