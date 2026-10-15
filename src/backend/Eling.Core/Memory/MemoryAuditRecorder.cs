using Eling.Core.Audit;

namespace Eling.Core.Memory;

/// <summary>
/// The memory domain's half of the audit trail: it turns the operations
/// <see cref="ScopedMemoryService"/> performs into change and access entries
/// (design §8 — ScopedMemoryService is the memory choke point) and hands them
/// to the injected <see cref="IAuditLogger"/>. Every entry carries the
/// operation's own <see cref="MemoryScopeKind"/> and the project's relative
/// label — the folder name, never the absolute path the service holds
/// (design §4.1, amendment A1) — and the logger resolves the ambient actor at
/// record time (§5), so no call site changes a signature to pass one.
/// Recording is fail-open by the logger's contract (§14): a recorder that
/// cannot write never fails the memory operation it is recording.
/// </summary>
internal sealed class MemoryAuditRecorder
{
    private readonly IAuditLogger _logger;

    private MemoryAuditRecorder(IAuditLogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// The recorder every construction site without a logger gets: it holds
    /// <see cref="NullAuditLogger.Instance"/>, so an unwired service performs no
    /// audit work at all rather than resolving the machine-global store by
    /// default.
    /// </summary>
    public static MemoryAuditRecorder Disabled { get; } = new(NullAuditLogger.Instance);

    public static MemoryAuditRecorder Create(IAuditLogger? logger) =>
        logger is null ? Disabled : new MemoryAuditRecorder(logger);

    /// <summary>
    /// <c>memory_save</c>: a create or a smart-save merge. The save result says
    /// which, and carries the before state when there is one, so an update
    /// records both sides without a second read.
    /// </summary>
    public ValueTask RecordSaveAsync(ScopedMemorySaveResult result) =>
        RecordChangeAsync(
            MemoryAuditActions.Save,
            result.Scope,
            result.ProjectRoot,
            result.Memory.Id,
            result.Memory,
            result.Previous?.Memory);

    /// <summary>
    /// <c>memory_save</c>: an explicit update, whose before state the caller
    /// read before writing over it.
    /// </summary>
    public ValueTask RecordUpdateAsync(ScopedMemory previous, ScopedMemory updated) =>
        RecordChangeAsync(
            MemoryAuditActions.Save,
            updated.Scope,
            updated.ProjectRoot,
            updated.Memory.Id,
            updated.Memory,
            previous.Memory);

    /// <summary>
    /// <c>memory_delete</c>: the removed state is the before-side, and nothing
    /// follows it (design §4.2).
    /// </summary>
    public ValueTask RecordDeleteAsync(ScopedMemory removed) =>
        RecordChangeAsync(
            MemoryAuditActions.Delete,
            removed.Scope,
            removed.ProjectRoot,
            removed.Memory.Id,
            after: null,
            removed.Memory);

    /// <summary>
    /// A copy into another tier: the target state only, because the source
    /// survives untouched (design §4.2).
    /// </summary>
    public ValueTask RecordCopiedAsync(string action, ScopedMemory target) =>
        RecordChangeAsync(
            action,
            target.Scope,
            target.ProjectRoot,
            target.Memory.Id,
            target.Memory,
            previous: null);

    /// <summary>
    /// A move or a promote into another tier: the target state plus the source
    /// state as the before-side, so the trail shows both sides of the tier
    /// switch (design §4.2). A promote keeps the source snapshot even when the
    /// source survives — that row is §4.2's contract for the promote action.
    /// </summary>
    public ValueTask RecordMovedAsync(string action, ScopedMemory source, ScopedMemory target) =>
        RecordChangeAsync(
            action,
            target.Scope,
            target.ProjectRoot,
            target.Memory.Id,
            target.Memory,
            source.Memory);

    /// <summary>
    /// <c>memory_get</c>: one memory read by id. The result count is the read
    /// itself — 1 when the memory was found, 0 when it was not.
    /// </summary>
    public ValueTask RecordGetAsync(MemoryScopeKind scope, string? projectRoot, MemoryId target, bool found) =>
        RecordAccessAsync(
            MemoryAuditActions.Get,
            scope,
            projectRoot,
            target,
            query: null,
            found ? 1 : 0);

    /// <summary>
    /// <c>memory_search</c>: the query text and how many hits it returned.
    /// </summary>
    public ValueTask RecordSearchAsync(string query, int resultCount) =>
        RecordAccessAsync(
            MemoryAuditActions.Search,
            scope: null,
            projectRoot: null,
            target: null,
            query,
            resultCount);

    /// <summary>
    /// <c>memory_list</c>: a listing, which has no query and no single target.
    /// </summary>
    public ValueTask RecordListAsync(int resultCount) =>
        RecordAccessAsync(
            MemoryAuditActions.List,
            scope: null,
            projectRoot: null,
            target: null,
            query: null,
            resultCount);

    private ValueTask RecordChangeAsync(
        string action,
        MemoryScopeKind scope,
        string? projectRoot,
        MemoryId target,
        Memory? after,
        Memory? previous) =>
        _logger.RecordAsync(new AuditEvent
        {
            Trail = AuditTrail.Change,
            Category = AuditCategory.Memory,
            Action = action,
            Scope = scope,
            ProjectRoot = ProjectLabel(projectRoot),
            Target = target.Value,
            Outcome = AuditOutcome.Success,
            Metadata = Snapshot(after),
            PreviousMetadata = Snapshot(previous)
        });

    private ValueTask RecordAccessAsync(
        string action,
        MemoryScopeKind? scope,
        string? projectRoot,
        MemoryId? target,
        string? query,
        int? resultCount) =>
        _logger.RecordAsync(new AuditEvent
        {
            Trail = AuditTrail.Access,
            Category = AuditCategory.Memory,
            Action = action,
            Scope = scope,
            ProjectRoot = ProjectLabel(projectRoot),
            Target = target?.Value,
            Query = query,
            ResultCount = resultCount,
            Outcome = AuditOutcome.Success
        });

    /// <summary>
    /// The change trail's state snapshot: content, tags, status and the memory's
    /// own source, and nothing else (design §4.2). Event detail such as query
    /// text and result counts never enters a snapshot.
    /// </summary>
    private static AuditMetadata? Snapshot(Memory? memory) => memory is null
        ? null
        : new AuditMetadata
        {
            Content = memory.Content,
            Tags = memory.Tags.ToArray(),
            Status = StatusLabel(memory.Status),
            MemorySource = memory.Source
        };

    /// <summary>
    /// The lower-case spelling the JSONL uses for a status, matching the
    /// canonical label vocabulary (amendment C1) rather than the enum member
    /// name a default converter would write.
    /// </summary>
    private static string StatusLabel(MemoryStatus status) => status switch
    {
        MemoryStatus.Superseded => "superseded",
        MemoryStatus.Archived => "archived",
        _ => "active"
    };

    /// <summary>
    /// The project label an entry carries: the folder name. The service holds
    /// full paths, and design §4.1 forbids a machine-specific absolute path in
    /// an entry — the label is the whole relativization amendment A1 asks for.
    /// A root with no folder name (a drive root) records nothing rather than an
    /// empty string.
    /// </summary>
    private static string? ProjectLabel(string? projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return null;
        }

        var label = Path.GetFileName(
            projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrEmpty(label) ? null : label;
    }
}
