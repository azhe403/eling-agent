namespace Eling.Core.Database.Browse;

/// <summary>
/// A user-safe validation failure of one browse request (unknown operator,
/// unknown column, unparseable numeric value, bad sort order). The message
/// names the offending column/operator and nothing about the connection.
/// </summary>
public sealed class BrowseQueryException(string message) : Exception(message);
