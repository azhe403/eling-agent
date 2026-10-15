using System.Text.Json.Serialization;
using Eling.Core.Audit;
using Eling.Core.Memory;

namespace Eling.Backend.Dtos;

/// <summary>
/// One audit entry as the dashboard reads it: the event detail every entry
/// carries, plus the change-only state snapshots and the access-only read
/// detail. Both trails share the shape — an access entry simply has no
/// <see cref="Metadata"/> and a change entry simply has no <see cref="Query"/>,
/// and null fields are omitted from the response, so the JSON stays as compact
/// as the JSONL it mirrors (design §9).
/// </summary>
public sealed record AuditEventDto(
    DateTimeOffset Timestamp,
    string Trail,
    string Category,
    string Action,
    string Actor,
    string Source,
    string Outcome,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Scope,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProjectRoot,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Target,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? DurationMs,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AuditMetadataDto? Metadata,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AuditMetadataDto? PreviousMetadata,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Query,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ResultCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Path,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ByteSize,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Matches)
{
    public static AuditEventDto From(Eling.Core.Audit.Storage.AuditRecord record)
    {
        var e = record.Event;
        return new AuditEventDto(
            e.Timestamp,
            AuditLabelsOf(e.Trail),
            AuditLabelsOf(e.Category),
            e.Action,
            e.Actor,
            e.Source,
            AuditLabelsOf(e.Outcome),
            e.Scope.HasValue ? MemoryScopeLabels.ToWireString(e.Scope.Value) : null,
            e.ProjectRoot,
            e.Target,
            e.DurationMs,
            e.Metadata is null ? null : AuditMetadataDto.From(e.Metadata),
            e.PreviousMetadata is null ? null : AuditMetadataDto.From(e.PreviousMetadata),
            e.Query,
            e.ResultCount,
            e.Path,
            e.ByteSize,
            e.Matches);
    }

    // The engine's own label vocabulary, spelled here rather than read from the
    // `internal` table the writer uses: the wire spelling of a trail, category
    // or outcome label is a contract, and Eling.Backend cannot see
    // Eling.Core's internals. Both are closed three-member enums, so each switch
    // is exhaustive and a new member is a compile error here rather than a
    // silently different label on the wire (amendment C1).
    private static string AuditLabelsOf(AuditTrail trail) => trail switch
    {
        AuditTrail.Change => "change",
        AuditTrail.Access => "access",
    };

    private static string AuditLabelsOf(AuditCategory category) => category switch
    {
        AuditCategory.Memory => "memory",
        AuditCategory.Runtime => "runtime",
        AuditCategory.Filesystem => "filesystem",
    };

    private static string AuditLabelsOf(AuditOutcome outcome) => outcome switch
    {
        AuditOutcome.Success => "success",
        AuditOutcome.Error => "error",
        AuditOutcome.Denied => "denied",
    };
}
