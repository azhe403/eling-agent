using System.ComponentModel;
using System.Diagnostics;
using Eling.Backend.Dtos;
using Eling.Core.Audit;
using Eling.Core.Database;
using Eling.Core.Database.Adapters;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Tools;

[McpServerToolType]
public sealed class DatabaseQueryTool
{
    private readonly IDatabaseConnectionStore _store;
    private readonly IDatabaseReadService _readService;
    private readonly AppSettingsConnectionScanner? _scanner;
    private readonly ILogger<DatabaseQueryTool>? _logger;
    private readonly IAuditLogger? _audit;

    public DatabaseQueryTool(
        IDatabaseConnectionStore store,
        IDatabaseReadService readService,
        AppSettingsConnectionScanner? scanner = null,
        ILogger<DatabaseQueryTool>? logger = null,
        IAuditLogger? audit = null)
    {
        _store = store;
        _readService = readService;
        _scanner = scanner;
        _logger = logger;
        _audit = audit;
    }

    [McpServerTool(Name = "db_query"), Description("Run a read-only SQL query (SELECT, EXPLAIN, PRAGMA, SHOW, DESCRIBE/DESC, and WITH CTEs) against a registered database connection. If the database cannot be reached, the last cached result is returned with fromCache=true and cachedAt set; a cached answer is never returned for a statement that was never run. If the connection is not registered, detect any unimported connections from appsettings.json and report them so they can be imported (with approval) before retrying.")]
    public async Task<QueryResult> QueryAsync(
        [Description("Connection id.")] string connectionId,
        [Description("SQL statement.")] string sql,
        [Description("Max rows (default 100).")] int maxRows = 100,
        [Description("Optional database name, for a server-level connection (one registered without a database).")] string? database = null,
        CancellationToken cancellationToken = default)
    {
        var row = await _store.FindAsync(connectionId, cancellationToken);
        if (row is null)
        {
            var hint = await BuildMissingConnectionHintAsync(cancellationToken);
            throw new KeyNotFoundException(string.IsNullOrEmpty(hint)
                ? "connection not found"
                : $"connection not found. {hint}");
        }

        if (row.Provider == DatabaseProvider.Redis)
        {
            throw new InvalidOperationException(RedisRequirement.NotSql(row, "use redis_scan"));
        }

        var classification = SqlSafety.Classify(sql);
        if (classification.Verdict != SqlVerdict.ReadOnly)
        {
            await (_audit?.RecordAsync(new AuditEvent
            {
                Trail = AuditTrail.Access,
                Category = AuditCategory.Database,
                Action = DatabaseAuditActions.Query,
                Outcome = AuditOutcome.Denied,
                Target = connectionId,
                Query = sql,
                Verb = classification.MutationVerb
            }, cancellationToken) ?? ValueTask.CompletedTask);
            var rejectionMessage = !string.IsNullOrWhiteSpace(classification.Reason)
                ? $"SQL rejected: {classification.Reason}."
                : classification.MutationVerb is not null
                    ? $"SQL rejected: '{classification.MutationVerb}' is a mutation statement. db_query is strictly read-only (SELECT, EXPLAIN, PRAGMA, SHOW, DESCRIBE, WITH)."
                    : "SQL rejected: statement is not a recognized read-only query. db_query only executes read-only operations.";
            throw new InvalidOperationException(rejectionMessage);
        }

        var sw = Stopwatch.StartNew();
        try
        {
            // Inside the try, so a stored connection string the provider
            // cannot read answers with the category below rather than the raw
            // failure escaping to the agent.
            if (maxRows < 1) maxRows = 1;
            if (maxRows > 1000) maxRows = 1000;
            var result = await _readService.QueryAsync(row, database, sql, maxRows, cancellationToken, preferCache: false);
            sw.Stop();
            await (_audit?.RecordAsync(new AuditEvent
            {
                Trail = AuditTrail.Access,
                Category = AuditCategory.Database,
                Action = DatabaseAuditActions.Query,
                Outcome = AuditOutcome.Success,
                Target = connectionId,
                Query = sql,
                ResultCount = result.Rows.Count,
                DurationMs = (int)sw.ElapsedMilliseconds,
                FromCache = result.FromCache
            }, cancellationToken) ?? ValueTask.CompletedTask);
            return result;
        }
        catch (DatabaseUnavailableException ex)
        {
            sw.Stop();
            _logger?.LogWarning(ex, "Query failed for {Id}", connectionId);
            await (_audit?.RecordAsync(new AuditEvent
            {
                Trail = AuditTrail.Access,
                Category = AuditCategory.Database,
                Action = DatabaseAuditActions.Query,
                Outcome = AuditOutcome.Error,
                Target = connectionId,
                Query = sql,
                DurationMs = (int)sw.ElapsedMilliseconds,
                FromCache = false
            }, cancellationToken) ?? ValueTask.CompletedTask);
            throw new InvalidOperationException($"query failed: {ex.Reason}");
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger?.LogWarning(ex, "Query failed for {Id}", connectionId);
            await (_audit?.RecordAsync(new AuditEvent
            {
                Trail = AuditTrail.Access,
                Category = AuditCategory.Database,
                Action = DatabaseAuditActions.Query,
                Outcome = AuditOutcome.Error,
                Target = connectionId,
                Query = sql,
                DurationMs = (int)sw.ElapsedMilliseconds
            }, cancellationToken) ?? ValueTask.CompletedTask);
            throw new InvalidOperationException("query failed");
        }
    }

    private async Task<string> BuildMissingConnectionHintAsync(CancellationToken cancellationToken)
    {
        if (_scanner is null) return string.Empty;
        try
        {
            var detected = _scanner.Scan();
            if (detected.Count == 0) return string.Empty;

            var registered = await _store.ListAsync(cancellationToken);
            var registeredNames = new HashSet<string>(registered.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);

            var unimported = detected
                .Where(d => d.Provider is not null && !registeredNames.Contains(d.Name))
                .Select(d => $"{d.SettingsKey} ({d.Name})")
                .ToList();

            if (unimported.Count == 0) return string.Empty;
            return $"Unimported connections detected in appsettings.json: {string.Join(", ", unimported)}. Offer the user options (import all / selected / skip) and run db_import_connections for the approved keys before retrying.";
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Detect failed while building missing-connection hint");
            return string.Empty;
        }
    }
}
