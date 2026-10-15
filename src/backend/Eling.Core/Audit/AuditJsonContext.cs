using System.Text.Json.Serialization;
using Eling.Core.Audit.Retention;
using Eling.Core.Memory;

namespace Eling.Core.Audit;

/// <summary>
/// Source-generated JSON metadata for the audit entry shape: one JSON line per entry.
/// </summary>
/// <remarks>
/// camelCase field names, single-line output and null omission keep each entry a compact
/// JSONL record. Enum values are kebab-case too, registered per enum below: the source
/// generator's <see cref="JsonSourceGenerationOptionsAttribute.UseStringEnumConverter"/> flag
/// has no naming policy to pass, so it would spell members as declared ("Change", "Memory").
/// Kebab-case keeps the scope labels canonical — "project-local" for
/// <see cref="MemoryScopeKind.ProjectLocal"/>, as <c>MemoryScopeLabels</c> and
/// <c>MemoryScopeParser</c> spell it.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters =
    [
        typeof(KebabCaseEnumConverter<AuditTrail>),
        typeof(KebabCaseEnumConverter<AuditCategory>),
        typeof(KebabCaseEnumConverter<AuditOutcome>),
        typeof(KebabCaseEnumConverter<MemoryScopeKind>)
    ])]
[JsonSerializable(typeof(AuditEvent))]
[JsonSerializable(typeof(AuditRollup))]
public partial class AuditJsonContext : JsonSerializerContext
{
}
