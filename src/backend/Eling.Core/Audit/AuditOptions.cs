namespace Eling.Core.Audit;

public sealed class AuditOptions
{
    public int ChangeRetentionDays { get; init; } = 365;
    public int AccessRetentionDays { get; init; } = 14;
    public int FlushBatchSize { get; init; } = 128;
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan FlushTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public bool CompressOnRotate { get; init; } = true;
    public bool CaptureQueryText { get; init; } = true;
}
