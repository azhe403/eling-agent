using System.Text.Json.Serialization;

namespace Eling.Backend.Dtos;

/// <summary>
/// One state snapshot of a change entry: the side of the transition the row
/// shows. Mirrors <see cref="Eling.Core.Audit.AuditMetadata"/> — content, tags,
/// status and memory source are the only state §4.2 allows in a snapshot, and
/// nothing else is carried over.
/// </summary>
public sealed record AuditMetadataDto(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Content,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Tags,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MemorySource)
{
    public static AuditMetadataDto From(Eling.Core.Audit.AuditMetadata state)
        => new(
            state.Content,
            state.Tags,
            state.Status,
            state.MemorySource);
}
