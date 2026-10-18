namespace Eling.Core.Database;

public sealed record ConnectionTestResult(
    bool Ok,
    string Message,
    long LatencyMs);