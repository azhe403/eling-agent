using Eling.Core.Audit;

namespace Eling.Core.Codebase;

/// <summary>
/// The codebase domain's half of the audit trail: it turns the explicit
/// invocations the MCP tools and the dashboard rebuild/search endpoints
/// perform into change and access entries (design §8, amendment B) and hands
/// them to the injected <see cref="IAuditLogger"/>. The background watcher
/// calls <see cref="CodebaseIndexService.IndexAsync"/> directly and never
/// passes through here, so its refreshes are unrecorded by construction
/// (design §12). Every entry carries the workspace's relative label — the
/// folder name, never the absolute root the service holds (design §4.1,
/// amendment A1) — and the logger resolves the ambient actor at record time
/// (§5), so no call site changes a signature to pass one. Change entries
/// carry no state snapshot: the action verb plus the target describes the
/// transition (amendment B2). Recording is fail-open by the logger's contract
/// (§14): a recorder that cannot write never fails the operation recorded.
/// </summary>
/// <remarks>
/// Public (unlike the memory domain's recorder) because the codebase callers
/// live in the backend host — the MCP tools and the dashboard endpoints —
/// while the memory callers sit in this same assembly.
/// </remarks>
public sealed class CodebaseAuditRecorder
{
    private readonly IAuditLogger _logger;

    private CodebaseAuditRecorder(IAuditLogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// The recorder every construction site without a logger gets: it holds
    /// <see cref="NullAuditLogger.Instance"/>, so an unwired caller performs no
    /// audit work at all rather than resolving the machine-global store by
    /// default.
    /// </summary>
    public static CodebaseAuditRecorder Disabled { get; } = new(NullAuditLogger.Instance);

    public static CodebaseAuditRecorder Create(IAuditLogger? logger) =>
        logger is null ? Disabled : new CodebaseAuditRecorder(logger);

    /// <summary>
    /// <c>codebase_index</c>: one explicit invocation — an MCP tool call or a
    /// dashboard rebuild request. A skipped single-flight pass is still an
    /// invocation (unlike an autonomous sweep, an explicit call is never
    /// self-noise), so it records with the outcome the caller observed.
    /// </summary>
    public ValueTask RecordIndexAsync(
        string projectRoot,
        int durationMs,
        AuditOutcome outcome = AuditOutcome.Success) =>
        _logger.RecordAsync(new AuditEvent
        {
            Trail = AuditTrail.Change,
            Category = AuditCategory.Codebase,
            Action = CodebaseAuditActions.Index,
            ProjectRoot = ProjectLabel(projectRoot),
            Target = ProjectLabel(projectRoot),
            Outcome = outcome,
            DurationMs = durationMs
        });

    /// <summary>
    /// <c>codebase_search</c>: the query text and how many hits it returned.
    /// File contents are never recorded (design §3: metadata only).
    /// </summary>
    public ValueTask RecordSearchAsync(
        string query,
        int resultCount,
        string projectRoot,
        int durationMs,
        AuditOutcome outcome = AuditOutcome.Success) =>
        _logger.RecordAsync(new AuditEvent
        {
            Trail = AuditTrail.Access,
            Category = AuditCategory.Codebase,
            Action = CodebaseAuditActions.Search,
            ProjectRoot = ProjectLabel(projectRoot),
            Target = ProjectLabel(projectRoot),
            Query = query,
            ResultCount = resultCount,
            Outcome = outcome,
            DurationMs = durationMs
        });

    /// <summary>
    /// <c>codebase_status</c>: an index-stats read, which has no query and no
    /// single target beyond the workspace itself.
    /// </summary>
    public ValueTask RecordStatusAsync(string projectRoot) =>
        _logger.RecordAsync(new AuditEvent
        {
            Trail = AuditTrail.Access,
            Category = AuditCategory.Codebase,
            Action = CodebaseAuditActions.Status,
            ProjectRoot = ProjectLabel(projectRoot),
            Target = ProjectLabel(projectRoot),
            Outcome = AuditOutcome.Success
        });

    /// <summary>
    /// The target label for a rebuild over several roots: the folder label of
    /// every root, joined. Single-root requests collapse to the one label;
    /// an empty set labels nothing rather than an empty string.
    /// </summary>
    public static string? TargetLabel(IEnumerable<string> roots)
    {
        var labels = roots.Select(ProjectLabel).Where(l => l is not null).ToList();
        return labels.Count == 0 ? null : string.Join(",", labels);
    }

    /// <summary>
    /// The workspace label an entry carries: the folder name. The service holds
    /// full paths, and design §4.1 forbids a machine-specific absolute path in
    /// an entry. A root with no folder name (a drive root) records nothing
    /// rather than an empty string.
    /// </summary>
    internal static string? ProjectLabel(string? projectRoot)
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
