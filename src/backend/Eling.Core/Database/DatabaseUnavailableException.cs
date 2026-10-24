namespace Eling.Core.Database;

/// <summary>
/// Thrown by a database adapter when it cannot reach the database — the host
/// is unreachable, the port is wrong, the credentials were rotated, or the
/// database file cannot be opened. The message names the failure category only;
/// it never carries the provider's own text, which can include the host name
/// and the user name.
/// </summary>
/// <remarks>
/// A failure while <em>executing</em> a statement is not an unavailable
/// database and must not be wrapped in this exception: the cache fallback
/// triggers on this type alone, and a wrong statement answered with cached
/// rows would hide the mistake.
/// </remarks>
public sealed class DatabaseUnavailableException : Exception
{
    /// <summary>
    /// A short, credential-free category describing why the database could
    /// not be reached (e.g. "host unreachable", "authentication failed",
    /// "connection timed out"). Never contains a host name or user name.
    /// </summary>
    public string Reason { get; }

    public DatabaseUnavailableException(string reason)
        : base($"database unavailable: {(string.IsNullOrWhiteSpace(reason) ? "unknown" : reason)}")
    {
        Reason = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason;
    }

    public DatabaseUnavailableException(string reason, Exception inner)
        : base($"database unavailable: {(string.IsNullOrWhiteSpace(reason) ? "unknown" : reason)}", inner)
    {
        Reason = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason;
    }
}