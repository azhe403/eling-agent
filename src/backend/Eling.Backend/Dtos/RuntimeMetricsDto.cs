namespace Eling.Backend.Dtos;

/// <summary>
/// One live process as sampled at one instant — the unit the metrics SSE
/// stream carries. Wire casing is camelCase like <see cref="RuntimeInfoDto"/>,
/// so the dashboard reads both streams the same way.
/// </summary>
/// <param name="Pid">The runtime's owning process.</param>
/// <param name="MemoryBytes">Working set bytes at sample time.</param>
/// <param name="CpuPercent">
/// CPU percent over the interval since this pid's previous sample; 100% means
/// every core busy. The tracker needs two samples at least 0.2s apart before
/// this is meaningful, so a pid's first frame legitimately reports 0.
/// </param>
/// <param name="SampledAt">When the batch was taken, so consumers can spot stale frames.</param>
public sealed record RuntimeMetricsSnapshot(
    int Pid,
    long MemoryBytes,
    double CpuPercent,
    DateTimeOffset SampledAt);
