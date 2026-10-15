using System.Text.Json.Serialization;

namespace Eling.Backend.Dtos;

/// <summary>
/// One page of the audit trail, newest first. <see cref="NextCursor"/> is the
/// timestamp of the page's last row and is returned only when more rows exist
/// in the requested window; a caller keeps passing it back until it comes back
/// null. It replaces the upper bound of the request's window rather than
/// indexing into it, so a row inserted between two page requests shifts nothing
/// (design §9). The window itself is echoed so the viewer can state exactly
/// which slice of history it is showing.
/// </summary>
public sealed record AuditEventListDto(
    IReadOnlyCollection<AuditEventDto> Items,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NextCursor,
    DateTimeOffset From,
    DateTimeOffset To,
    int Limit);
