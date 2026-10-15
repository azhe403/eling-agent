using System.Text.Json;
using System.Text.Json.Serialization;

namespace Eling.Core.Audit;

/// <summary>
/// String-enum converter that writes and reads members in camelCase, matching the
/// lowercase enum values the audit trail stores on disk ("change", "memory", "project").
/// </summary>
/// <remarks>
/// <see cref="JsonSourceGenerationOptionsAttribute.UseStringEnumConverter"/> has no naming
/// policy to carry, so it spells members as declared ("Change", "Memory", "Project"); this
/// converter is registered per enum in <see cref="AuditJsonContext"/> instead. It is generic
/// so one class covers <see cref="AuditTrail"/>, <see cref="AuditCategory"/>,
/// <see cref="AuditOutcome"/> and <see cref="Eling.Core.Memory.MemoryScopeKind"/>, which cannot
/// carry an attribute of its own because the audit trail does not own that type.
/// </remarks>
internal sealed class CamelCaseEnumConverter<TEnum> : JsonStringEnumConverter<TEnum>
    where TEnum : struct, Enum
{
    public CamelCaseEnumConverter()
        : base(JsonNamingPolicy.CamelCase)
    {
    }
}
