using Eling.Core.Memory;

namespace Eling.Core.Audit;

public sealed class AuditEvent
{
    public int SchemaVersion { get; init; } = 1;
    public DateTimeOffset Timestamp { get; init; }
    public AuditTrail Trail { get; init; }
    public AuditCategory Category { get; init; }
    public string Action { get; init; } = string.Empty;
    public string Actor { get; init; } = AuditActor.Unknown.Actor;
    public string Source { get; init; } = AuditActor.Unknown.Source;
    public MemoryScopeKind? Scope { get; init; }
    public string? ProjectRoot { get; init; }
    public string? Target { get; init; }
    public AuditOutcome Outcome { get; init; } = AuditOutcome.Success;
    public int? DurationMs { get; init; }
    public long? ByteSize { get; init; }
    public string? Hash { get; init; }
    public string? PrevHash { get; init; }
    // Access-only detail; null for other entries.
    public string? Query { get; init; }
    public int? ResultCount { get; init; }
    public string? Path { get; init; }
    public int? Matches { get; init; }
    public int? Pid { get; init; }
    // Change-trail state snapshots.
    public AuditMetadata? Metadata { get; init; }
    public AuditMetadata? PreviousMetadata { get; init; }
}
