namespace Eling.Core.Database;

/// <summary>
/// The <see cref="DatabaseSchemaInfo.Source"/> values. Strings rather than an
/// enum: the dashboard and the MCP output both read them verbatim, and the
/// cache file serializes them without a string-enum converter.
/// </summary>
public static class CacheSources
{
    /// <summary>The database was read; the answer is current.</summary>
    public const string Live = "live";

    /// <summary>
    /// A cached snapshot was served and a refresh is running in the background.
    /// </summary>
    public const string Refreshing = "refreshing";

    /// <summary>
    /// The database could not be reached and the cached snapshot answered
    /// instead. The snapshot's remaining lifetime is in
    /// <see cref="DatabaseSchemaInfo.CacheTtlSeconds"/>.
    /// </summary>
    public const string Offline = "offline";
}
