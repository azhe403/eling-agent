using System.Text.Json.Serialization;
using Eling.Core.Memory;

namespace Eling.Core.Audit;

/// <summary>
/// Source-generated JSON metadata for the audit entry shape: one JSON line per entry.
/// </summary>
/// <remarks>
/// camelCase field names, single-line output and null omission keep each entry a compact
/// JSONL record. Enum values are camelCase too, registered per enum below: the source
/// generator's <see cref="JsonSourceGenerationOptionsAttribute.UseStringEnumConverter"/> flag
/// has no naming policy to pass, so it would spell members as declared ("Change", "Memory").
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters =
    [
        typeof(CamelCaseEnumConverter<AuditTrail>),
        typeof(CamelCaseEnumConverter<AuditCategory>),
        typeof(CamelCaseEnumConverter<AuditOutcome>),
        typeof(CamelCaseEnumConverter<MemoryScopeKind>)
    ])]
[JsonSerializable(typeof(AuditEvent))]
public partial class AuditJsonContext : JsonSerializerContext
{
}
