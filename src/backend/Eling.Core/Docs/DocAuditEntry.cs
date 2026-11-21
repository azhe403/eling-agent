namespace Eling.Core.Docs;

/// <summary>
/// Audit entry for document mutations.
/// </summary>
public sealed record DocAuditEntry(
    string Id,
    string DocId,
    string Actor,
    string Action,
    string? BeforeJson,
    string? AfterJson,
    DateTimeOffset At);