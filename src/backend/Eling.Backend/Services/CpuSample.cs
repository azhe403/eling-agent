namespace Eling.Backend.Services;

internal sealed record CpuSample(
    DateTimeOffset Timestamp,
    TimeSpan CpuTime);
