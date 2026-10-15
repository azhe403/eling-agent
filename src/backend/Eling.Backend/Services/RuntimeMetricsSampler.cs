using Eling.Backend.Dtos;

namespace Eling.Backend.Services;

/// <summary>
/// Turns the currently live runtimes into a metrics batch. Stateless on
/// purpose: the CPU delta lives in <see cref="ProcessMetricsTracker"/>'s
/// per-pid sample cache, so callers only pay for the probes they trigger and
/// tests can build one per assertion.
/// </summary>
public sealed class RuntimeMetricsSampler
{
    private readonly RuntimeRegistry _registry;
    private readonly ProcessMetricsTracker _metricsTracker;
    private readonly ILogger<RuntimeMetricsSampler>? _logger;

    public RuntimeMetricsSampler(
        RuntimeRegistry registry,
        ProcessMetricsTracker metricsTracker,
        ILogger<RuntimeMetricsSampler>? logger = null)
    {
        _registry = registry;
        _metricsTracker = metricsTracker;
        _logger = logger;
    }

    /// <summary>
    /// Every live runtime that yielded a probe, or an empty batch when nothing
    /// is running. A pid whose probe fails — the process exited between the
    /// registry read and the probe — is omitted rather than failing the batch;
    /// the tracker already degrades that way, and the real membership change
    /// arrives separately on the topic SSE stream.
    /// </summary>
    public IReadOnlyList<RuntimeMetricsSnapshot> SampleAll()
    {
        var sampledAt = DateTimeOffset.UtcNow;
        var batch = new List<RuntimeMetricsSnapshot>();

        foreach (var runtime in _registry.Alive(allProcesses: true))
        {
            if (!runtime.IsAlive || runtime.ProcessId <= 0) continue;

            var metrics = _metricsTracker.Sample(runtime.ProcessId);
            if (metrics is null) continue;

            batch.Add(new RuntimeMetricsSnapshot(
                runtime.ProcessId, metrics.MemoryBytes, metrics.CpuPercent, sampledAt));
        }

        _logger?.LogDebug("Sampled {Count} runtime metrics", batch.Count);
        return batch;
    }
}
