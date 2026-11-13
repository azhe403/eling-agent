namespace Eling.Core.CommitLease;

/// <summary>
/// Cross-process change signal for the lease store, mirroring
/// <c>Eling.Core.Memory.IMemoryChangeNotifier</c>. Fire-and-forget by
/// contract: implementations never throw, and the store calls it after the
/// mutation has been durably saved (never while holding the storage gate).
/// </summary>
public interface ICommitLeaseChangeNotifier
{
    Task NotifyAsync(CancellationToken ct = default);
}
