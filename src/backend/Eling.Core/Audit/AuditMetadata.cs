namespace Eling.Core.Audit;

public sealed class AuditMetadata
{
    public string? Content { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public string? Status { get; init; }
    public string? MemorySource { get; init; }
}
