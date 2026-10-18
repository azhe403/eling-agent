using Eling.Backend.Dtos;
using Eling.Core.Database;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Database;

public sealed class DatabaseConnectionImporter
{
    private readonly AppSettingsConnectionScanner _scanner;
    private readonly IDatabaseConnectionStore _store;
    private readonly ILogger<DatabaseConnectionImporter>? _logger;

    public DatabaseConnectionImporter(
        AppSettingsConnectionScanner scanner,
        IDatabaseConnectionStore store,
        ILogger<DatabaseConnectionImporter>? logger = null)
    {
        _scanner = scanner;
        _store = store;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ImportResultDto>> ImportAsync(
        IReadOnlyList<string> settingsKeys,
        CancellationToken ct = default)
    {
        var results = new List<ImportResultDto>();
        var candidates = _scanner.Scan();
        var existing = await _store.ListAsync(ct);
        var existingNames = new HashSet<string>(
            existing.Select(e => e.Name),
            StringComparer.OrdinalIgnoreCase);

        foreach (var key in settingsKeys)
        {
            var candidate = candidates.FirstOrDefault(c =>
                string.Equals(c.SettingsKey, key, StringComparison.Ordinal));

            if (candidate is null)
            {
                results.Add(new ImportResultDto(key, null, null, false, "not found in source"));
                continue;
            }

            if (candidate.Provider is null)
            {
                results.Add(new ImportResultDto(key, null, null, false, candidate.RejectionReason));
                continue;
            }

            var name = candidate.Name;
            try
            {
                if (existingNames.Contains(name))
                {
                    results.Add(new ImportResultDto(key, null, name, false, "already exists"));
                    continue;
                }

                var conn = DatabaseConnection.Create(
                    name,
                    candidate.Provider.Value,
                    candidate.ConnectionString,
                    DatabaseEnvironment.ReadOnly,
                    allowAgentMutations: false,
                    requireApprovalForMutations: true);

                await _store.UpsertAsync(conn, ct);
                existingNames.Add(name);
                results.Add(new ImportResultDto(key, conn.Id, conn.Name, true, null));
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Import failed for key {Key}.", key);
                results.Add(new ImportResultDto(key, null, name, false, ex.Message));
            }
        }

        return results;
    }
}
