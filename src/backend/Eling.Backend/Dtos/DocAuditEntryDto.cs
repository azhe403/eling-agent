namespace Eling.Backend.Dtos;

/// <summary>
/// Document audit entry DTO.
/// </summary>
public sealed record DocAuditEntryDto(
    string Id,
    string DocId,
    string Actor,
    string Action,
    string? BeforeJson,
    string? AfterJson,
    DateTimeOffset At)
{
    public static DocAuditEntryDto From(Eling.Core.Docs.DocAuditEntry entry) => new(
        entry.Id,
        entry.DocId,
        entry.Actor,
        entry.Action,
        entry.BeforeJson,
        entry.AfterJson,
        entry.At);
}