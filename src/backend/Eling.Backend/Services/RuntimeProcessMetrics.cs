namespace Eling.Backend.Services;

public sealed record RuntimeProcessMetrics(
    long MemoryBytes,
    double CpuPercent);
