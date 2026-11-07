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
    /// The snapshot answered, and the database has not been read yet. The
    /// caller shows it at once and asks again for the real answer.
    /// </summary>
    public const string Cached = "cached";

    /// <summary>
    /// The database could not be reached and the cached snapshot answered
    /// instead. The snapshot's remaining lifetime is in
    /// <see cref="DatabaseSchemaInfo.CacheTtlSeconds"/>.
    /// </summary>
    public const string Offline = "offline";
}
