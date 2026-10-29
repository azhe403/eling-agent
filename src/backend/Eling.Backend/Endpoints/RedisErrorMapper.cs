namespace Eling.Backend.Endpoints;

/// <summary>
/// The classifier the key routes use to answer "which HTTP status does this
/// Redis failure deserve".
///
/// <para>
/// A server that rejects the connection's own credentials (NOAUTH / WRONGPASS)
/// is not "down" - it is telling us the stored password is wrong. That is a
/// 502, the dashboard's "our configuration is wrong" answer. Anything else
/// that comes out of Redis is a 503.
/// </para>
/// </summary>
public static class RedisErrorMapper
{
    /// <summary>
    /// True when the exception chain says the server refused the connection's
    /// credentials.
    ///
    /// <para>
    /// StackExchange.Redis surfaces credential rejection in two shapes: a
    /// connection failure whose message carries the server's reason, and a
    /// command failure that the connection layer rethrew. Both carry the
    /// reason text, so matching it is enough.
    /// </para>
    /// </summary>
    public static bool IsRedisCredentialFailure(this Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            var msg = e.Message;
            if (msg.Contains("NOAUTH", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("WRONGPASS", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
