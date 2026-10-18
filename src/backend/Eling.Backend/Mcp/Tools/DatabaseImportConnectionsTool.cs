using System.ComponentModel;
using Eling.Backend.Database;
using Eling.Backend.Dtos;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Tools;

[McpServerToolType]
public sealed class DatabaseImportConnectionsTool
{
    private readonly DatabaseConnectionImporter _importer;
    private readonly ILogger<DatabaseImportConnectionsTool>? _logger;

    public DatabaseImportConnectionsTool(
        DatabaseConnectionImporter importer,
        ILogger<DatabaseImportConnectionsTool>? logger = null)
    {
        _importer = importer;
        _logger = logger;
    }

    [McpServerTool(Name = "db_import_connections"), Description("Import approved connection strings (by settings key) from appsettings.json into the registry as read-only connections. The user must explicitly approve which keys to import; detect first, ask, then import exactly the approved keys.")]
    public Task<IReadOnlyList<ImportResultDto>> ImportAsync(
        [Description("Settings keys to import.")] string[] settingsKeys,
        CancellationToken cancellationToken = default)
    {
        return _importer.ImportAsync(settingsKeys ?? [], cancellationToken);
    }
}
