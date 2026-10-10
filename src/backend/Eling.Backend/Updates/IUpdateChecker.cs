namespace Eling.Backend.Updates;

/// <summary>
/// Cached update status with an explicit refresh: <c>GetStatusAsync</c> never
/// touches the network, <c>CheckNowAsync</c> fetches only when the cache is
/// stale. Both never throw for network reasons.
/// </summary>
public interface IUpdateChecker
{
    /// <summary>Last known status without any network traffic.</summary>
    Task<UpdateStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Refreshes the status when the cache is stale, otherwise the cache.</summary>
    Task<UpdateStatus> CheckNowAsync(CancellationToken cancellationToken = default);
}
