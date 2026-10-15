using Eling.Core.Audit;
using Eling.Core.Audit.Storage;

namespace Eling.Backend.Mcp;

/// <summary>
/// The audit trail's writer as the DI container sees it. The engine's
/// <see cref="BufferedAuditLogger"/> drains its channel only through
/// <c>DisposeAsync</c>, and a container disposed synchronously — a test host's
/// <c>BuildServiceProvider().Dispose()</c>, and any host that disposes its
/// provider without awaiting — refuses to dispose an async-only singleton at all,
/// which would leave the pump running past shutdown and the pending entries
/// unwritten. This wrapper exposes both disposal shapes, so whichever way a host
/// tears itself down, the pending entries still land (design §7.3).
/// </summary>
public sealed class DisposableAuditLogger(BufferedAuditLogger inner) : IAuditLogger, IAsyncDisposable, IDisposable
{
    public ValueTask RecordAsync(AuditEvent entry, CancellationToken cancellationToken = default) =>
        inner.RecordAsync(entry, cancellationToken);

    public ValueTask DisposeAsync() => inner.DisposeAsync();

    /// <summary>
    /// Waits for the same drain the asynchronous path performs, bounded by the
    /// engine's own budget and never throwing: the engine's drain is itself best
    /// effort, and the access trail is fire-and-forget by design (§7.3), so a
    /// host that cannot await disposal loses at most that tail.
    /// </summary>
    public void Dispose()
    {
        try
        {
            inner.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Fail-open, exactly as the engine's own shutdown drain: a failure
            // here must never break the host that is shutting down.
        }
    }
}
