using System.Text.RegularExpressions;
using Xunit;

namespace Eling.Backend.Tests;

/// <summary>
/// Enforces that private fields use descriptive domain names rather than generic
/// suffix names (e.g. _docsIndex instead of _index).
/// Uses pattern matching against the type's PascalCase name without hardcoded banned words.
/// Legacy violations are tracked in an allowlist to be burned down over time.
/// </summary>
public sealed class FieldNamingConventionTests
{
    private static readonly Regex PrivateFieldRegex = new(
        @"^\s*private\s+(?:readonly\s+)?(?<type>[A-Za-z0-9_<>?]+)\s+(?<name>_[A-Za-z0-9_]+)\s*(?:=.*)?;\s*$",
        RegexOptions.Compiled);

    private static readonly Regex PascalWordsRegex = new(
        @"[A-Z][a-z0-9]*",
        RegexOptions.Compiled);

    private static readonly HashSet<string> IgnoredTypePrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ILogger",
        "Action",
        "Func",
        "Task",
        "CancellationToken",
        "List",
        "Dictionary",
        "HashSet",
        "IEnumerable",
        "IReadOnlyList",
        "IReadOnlyCollection",
        "IReadOnlySet",
        "DateTime",
        "DateTimeOffset",
        "TimeSpan",
        "StringBuilder",
        "StringComparison",
        "SemaphoreSlim",
        "FileStream",
        "StreamWriter",
        "StreamReader",
        "MemoryStream",
        "FileSystemWatcher",
        "HttpClient",
        "HttpContext",
        "RequestDelegate",
        "ConcurrentQueue",
        "ConcurrentDictionary",
        "Timer",
    };

    private static readonly HashSet<string> PlumbingFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "_lock",
        "_gate",
        "_syncRoot",
        "_cts",
        "_stopping",
        "_disposed",
    };

    /// <summary>
    /// Temporary allowlist for legacy fields before this convention was enforced.
    /// Format: "relativePath:fieldName".
    /// Burn down and remove over time. New code must NOT be added here.
    /// </summary>
    private static readonly HashSet<string> LegacyExemptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "src/backend/Eling.Backend/AuditDirWatcher.cs:_broadcaster",
        "src/backend/Eling.Backend/CodebaseDirWatcher.cs:_broadcaster",
        "src/backend/Eling.Backend/DatabaseRegistryWatcher.cs:_broadcaster",
        "src/backend/Eling.Backend/RuntimeDirWatcher.cs:_broadcaster",
        "src/backend/Eling.Backend/RuntimeRegistry.cs:_globalService",
        "src/backend/Eling.Backend/RuntimeRegistry.cs:_merger",
        "src/backend/Eling.Backend/RuntimeRegistry.cs:_broadcaster",
        "src/backend/Eling.Backend/SkillCatalogDirWatcher.cs:_scanner",
        "src/backend/Eling.Backend/SkillCatalogDirWatcher.cs:_broadcaster",
        "src/backend/Eling.Backend/Bootstrap/CodebaseWatcherService.cs:_svc",
        "src/backend/Eling.Backend/Bootstrap/CodebaseWatcherService.cs:_notifier",
        "src/backend/Eling.Backend/Bootstrap/CodebaseWatcherService.cs:_ignores",
        "src/backend/Eling.Backend/Bootstrap/HttpLoopService.cs:_shared",
        "src/backend/Eling.Backend/Bootstrap/HttpLoopService.cs:_context",
        "src/backend/Eling.Backend/Bootstrap/HttpLoopService.cs:_options",
        "src/backend/Eling.Backend/Bootstrap/RuntimeRegistrationService.cs:_registry",
        "src/backend/Eling.Backend/Bootstrap/RuntimeRegistrationService.cs:_context",
        "src/backend/Eling.Backend/Bootstrap/StartupTimingHostedService.cs:_lifetime",
        "src/backend/Eling.Backend/Codebase/CodebaseRebuildJobRunner.cs:_local",
        "src/backend/Eling.Backend/Codebase/CodebaseRebuildJobRunner.cs:_broadcaster",
        "src/backend/Eling.Backend/Codebase/CodebaseRebuildJobRunner.cs:_notifier",
        "src/backend/Eling.Backend/Codebase/CodebaseRebuildJobRunner.cs:_request",
        "src/backend/Eling.Backend/Database/DatabaseConnectionImporter.cs:_scanner",
        "src/backend/Eling.Backend/Database/DatabaseConnectionImporter.cs:_store",
        "src/backend/Eling.Backend/Judging/ProviderSemanticJudge.cs:_store",
        "src/backend/Eling.Backend/Judging/ProviderSemanticJudge.cs:_pipeline",
        "src/backend/Eling.Backend/Judging/SemanticJudgeStore.cs:_config",
        "src/backend/Eling.Backend/Logging/LogConfigStore.cs:_format",
        "src/backend/Eling.Backend/Logging/LogConfigStore.cs:_stamp",
        "src/backend/Eling.Backend/Logging/LogFormatForwardingSink.cs:_inner",
        "src/backend/Eling.Backend/Logging/LogFormatSwitcher.cs:_forwarder",
        "src/backend/Eling.Backend/Mcp/DailyLogRollerService.cs:_sink",
        "src/backend/Eling.Backend/Services/CommitLeaseBroadcaster.cs:_topics",
        "src/backend/Eling.Backend/Services/ProjectScopeCatalog.cs:_registry",
        "src/backend/Eling.Backend/Services/RuntimeMetricsSampler.cs:_registry",
        "src/backend/Eling.Backend/Services/RuntimeMetricsSampler.cs:_metricsTracker",
        "src/backend/Eling.Backend/Shell/ShellConfigStore.cs:_config",
        "src/backend/Eling.Backend/Shell/ShellConfigStore.cs:_stamp",
        "src/backend/Eling.Backend/Tools/ToolPolicyStore.cs:_config",
        "src/backend/Eling.Backend/Tools/ToolPolicyStore.cs:_stamp",
        "src/backend/Eling.Backend/Updates/FileUpdateCache.cs:_status",
        "src/backend/Eling.Backend/Updates/UpdateChecker.cs:_client",
        "src/backend/Eling.Backend/Updates/UpdateChecker.cs:_cache",
        "src/backend/Eling.Backend/Updates/UpdateCheckerService.cs:_checker",
        "src/backend/Eling.Core/Codebase/CodebaseAuditRecorder.cs:_logger",
        "src/backend/Eling.Core/Codebase/CodebaseIndexService.cs:_index",
        "src/backend/Eling.Core/CommitLease/JsonCommitLeaseStore.cs:_notifier",
        "src/backend/Eling.Core/Database/DatabaseReadService.cs:_cache",
        "src/backend/Eling.Core/Database/ElasticsearchReadService.cs:_cache",
        "src/backend/Eling.Core/Database/JsonDatabaseReadCache.cs:_schemaGate",
        "src/backend/Eling.Core/Database/JsonDatabaseReadCache.cs:_queryGate",
        "src/backend/Eling.Core/Database/RedisKeyReadService.cs:_pool",
        "src/backend/Eling.Core/Database/RedisKeyReadService.cs:_cache",
        "src/backend/Eling.Core/Docs/DocsService.cs:_index",
        "src/backend/Eling.Core/Docs/DocsService.cs:_scopeResolver",
        "src/backend/Eling.Core/Logging/HostingFailureDemotingSink.cs:_inner",
        "src/backend/Eling.Core/Logging/RollingDailyFileSink.cs:_formatter",
        "src/backend/Eling.Core/Logging/RollingDailyFileSink.cs:_format",
        "src/backend/Eling.Core/Memory/MemoryAuditRecorder.cs:_logger",
        "src/backend/Eling.Core/Memory/MemoryService.cs:_storage",
        "src/backend/Eling.Core/Memory/MemoryService.cs:_index",
        "src/backend/Eling.Core/Memory/MemoryService.cs:_judge",
        "src/backend/Eling.Core/Memory/ScopedMemoryService.cs:_globalService",
        "src/backend/Eling.Core/Memory/ScopedMemoryService.cs:_localService",
        "src/backend/Eling.Core/Memory/ScopedMemoryService.cs:_policy",
        "src/backend/Eling.Core/Memory/ScopedMemoryService.cs:_merger",
        "src/backend/Eling.Core/Memory/ScopedMemoryService.cs:_audit",
        "src/backend/Eling.Core/Skill/Skill.cs:_source",
        "src/backend/Eling.Core/Skill/SkillService.cs:_storage",
        "src/backend/Eling.Core/Skill/SkillService.cs:_index",
        "src/backend/Eling.Core/Skill/SkillService.cs:_notifier",
        "src/backend/Eling.Backend/Mcp/Tools/CodebaseIndexTool.cs:_svc",
        "src/backend/Eling.Backend/Mcp/Tools/CodebaseIndexTool.cs:_notifier",
        "src/backend/Eling.Backend/Mcp/Tools/CodebaseIndexTool.cs:_audit",
        "src/backend/Eling.Backend/Mcp/Tools/CodebaseSearchTool.cs:_svc",
        "src/backend/Eling.Backend/Mcp/Tools/CodebaseSearchTool.cs:_registry",
        "src/backend/Eling.Backend/Mcp/Tools/CodebaseSearchTool.cs:_audit",
        "src/backend/Eling.Backend/Mcp/Tools/CodebaseStatusTool.cs:_svc",
        "src/backend/Eling.Backend/Mcp/Tools/CodebaseStatusTool.cs:_watcher",
        "src/backend/Eling.Backend/Mcp/Tools/CodebaseStatusTool.cs:_audit",
        "src/backend/Eling.Backend/Mcp/Tools/CommitLeaseTool.cs:_leaseStore",
        "src/backend/Eling.Backend/Mcp/Tools/CommitListActiveTool.cs:_leaseStore",
        "src/backend/Eling.Backend/Mcp/Tools/CommitReleaseTool.cs:_leaseStore",
        "src/backend/Eling.Backend/Mcp/Tools/CommitWaitTool.cs:_leaseStore",
        "src/backend/Eling.Backend/Mcp/Tools/DatabaseDetectConnectionsTool.cs:_scanner",
        "src/backend/Eling.Backend/Mcp/Tools/DatabaseGetSchemaTool.cs:_store",
        "src/backend/Eling.Backend/Mcp/Tools/DatabaseGetSchemaTool.cs:_readService",
        "src/backend/Eling.Backend/Mcp/Tools/DatabaseImportConnectionsTool.cs:_importer",
        "src/backend/Eling.Backend/Mcp/Tools/DatabaseListConnectionsTool.cs:_store",
        "src/backend/Eling.Backend/Mcp/Tools/DatabaseListConnectionsTool.cs:_scanner",
        "src/backend/Eling.Backend/Mcp/Tools/DatabaseQueryTool.cs:_store",
        "src/backend/Eling.Backend/Mcp/Tools/DatabaseQueryTool.cs:_readService",
        "src/backend/Eling.Backend/Mcp/Tools/DatabaseQueryTool.cs:_scanner",
        "src/backend/Eling.Backend/Mcp/Tools/EsCountTool.cs:_store",
        "src/backend/Eling.Backend/Mcp/Tools/EsCountTool.cs:_readService",
        "src/backend/Eling.Backend/Mcp/Tools/EsIndicesTool.cs:_store",
        "src/backend/Eling.Backend/Mcp/Tools/EsIndicesTool.cs:_readService",
        "src/backend/Eling.Backend/Mcp/Tools/EsMappingTool.cs:_store",
        "src/backend/Eling.Backend/Mcp/Tools/EsMappingTool.cs:_readService",
        "src/backend/Eling.Backend/Mcp/Tools/EsSearchTool.cs:_store",
        "src/backend/Eling.Backend/Mcp/Tools/EsSearchTool.cs:_readService",
        "src/backend/Eling.Backend/Mcp/Tools/FileSystemTools.cs:_fs",
        "src/backend/Eling.Backend/Mcp/Tools/MemoryMaintenanceTool.cs:_maintenance",
        "src/backend/Eling.Backend/Mcp/Tools/MemoryProjectScopePolicyTool.cs:_store",
        "src/backend/Eling.Backend/Mcp/Tools/MemoryProjectStatusTool.cs:_policyStore",
        "src/backend/Eling.Backend/Mcp/Tools/MemoryPromoteTool.cs:_notifier",
        "src/backend/Eling.Backend/Mcp/Tools/MemoryWriteTool.cs:_notifier",
        "src/backend/Eling.Backend/Mcp/Tools/MemoryWriteTool.cs:_policyStore",
        "src/backend/Eling.Backend/Mcp/Tools/RecallTool.cs:_policyStore",
        "src/backend/Eling.Backend/Mcp/Tools/RecallTool.cs:_summarizer",
        "src/backend/Eling.Backend/Mcp/Tools/RecallTool.cs:_watcher",
        "src/backend/Eling.Backend/Mcp/Tools/RedisInspectTool.cs:_store",
        "src/backend/Eling.Backend/Mcp/Tools/RedisInspectTool.cs:_readService",
        "src/backend/Eling.Backend/Mcp/Tools/RedisScanTool.cs:_store",
        "src/backend/Eling.Backend/Mcp/Tools/RedisScanTool.cs:_readService",
        "src/backend/Eling.Backend/Mcp/Tools/ShellTools.cs:_store",
        "src/backend/Eling.Backend/Mcp/Tools/ShellTools.cs:_sessionRoot",
        "src/backend/Eling.Backend/Mcp/Tools/ToolsPolicyTool.cs:_store",
        "src/backend/Eling.Core/Audit/Storage/BufferedAuditLogger.cs:_store",
        "src/backend/Eling.Core/Audit/Storage/BufferedAuditLogger.cs:_index",
        "src/backend/Eling.Core/Audit/Storage/BufferedAuditLogger.cs:_options",
        "src/backend/Eling.Core/Database/Adapters/MySqlDatabaseAdapter.cs:_connection",
        "src/backend/Eling.Core/Database/Adapters/PostgreSqlDatabaseAdapter.cs:_connection",
        "src/backend/Eling.Core/Database/Adapters/SqliteDatabaseAdapter.cs:_connection",
        "src/backend/Eling.Core/Skill/Scanning/SkillCatalogScanner.cs:_index",
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Eling.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException("Repo root (Eling.slnx) not found from test output.");
    }

    private static string Relative(string repoRoot, string fullPath)
        => Path.GetRelativePath(repoRoot, fullPath).Replace(Path.DirectorySeparatorChar, '/');

    public static string CleanTypeName(string rawType)
    {
        var name = rawType.TrimEnd('?');
        var genericIdx = name.IndexOf('<');
        if (genericIdx > 0) name = name[..genericIdx];

        // Strip interface prefix 'I' if followed by PascalCase (e.g. IDocsIndex -> DocsIndex)
        if (name.StartsWith('I') && name.Length > 2 && char.IsUpper(name[1]) && char.IsLower(name[2]))
            name = name[1..];

        return name;
    }

    [Fact]
    public void PrivateFields_MustIncludeDomainPartOfType()
    {
        var repoRoot = RepoRoot();
        var backendDir = Path.Combine(repoRoot, "src", "backend");
        var allBackendFiles = Directory.EnumerateFiles(backendDir, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

        var offenders = new List<string>();

        foreach (var file in allBackendFiles)
        {
            var rel = Relative(repoRoot, file);
            var lineNo = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNo++;
                var match = PrivateFieldRegex.Match(line);
                if (!match.Success) continue;

                var rawType = match.Groups["type"].Value;
                var fieldName = match.Groups["name"].Value;

                if (PlumbingFieldNames.Contains(fieldName)) continue;
                if (IgnoredTypePrefixes.Any(p => rawType.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;

                // Check temporary legacy allowlist
                if (LegacyExemptions.Contains($"{rel}:{fieldName}")) continue;

                var cleanType = CleanTypeName(rawType);
                var words = PascalWordsRegex.Matches(cleanType).Select(m => m.Value).ToList();

                // If type has at least 2 words (e.g. Docs + Index, Docs + Service),
                // field name must contain the first (domain) word.
                if (words.Count >= 2)
                {
                    var domainWord = words[0];
                    if (!fieldName.Contains(domainWord, StringComparison.OrdinalIgnoreCase))
                    {
                        offenders.Add(
                            $"{rel}:{lineNo}: field '{fieldName}' of type '{rawType}' missing domain '{domainWord}' (expected e.g. '_{char.ToLowerInvariant(domainWord[0])}{domainWord[1..]}{words[^1]}')");
                    }
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Field naming convention violated (new fields must include domain word from Type):\n" + string.Join("\n", offenders));
    }
}
