using System.ComponentModel;
using Eling.Backend.Dtos;
using Eling.Core.Database;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Tools;

[McpServerToolType]
public sealed class DatabaseDetectConnectionsTool
{
    private readonly AppSettingsConnectionScanner _scanner;
    private readonly ILogger<DatabaseDetectConnectionsTool>? _logger;

    public DatabaseDetectConnectionsTool(
        AppSettingsConnectionScanner scanner,
        ILogger<DatabaseDetectConnectionsTool>? logger = null)
    {
        _scanner = scanner;
        _logger = logger;
    }

    [McpServerTool(Name = "db_detect_connections"), Description("Preview database connection strings found in the backend appsettings.json (passwords masked). Writes nothing.")]
    public Task<IReadOnlyList<McpDetectedCandidate>> DetectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var detected = _scanner.Scan();
            var result = detected.Select((d, i) => new McpDetectedCandidate(
                i + 1,
                d.SettingsKey,
                d.Name,
                d.Provider,
                d.RejectionReason)).ToList();
            return Task.FromResult<IReadOnlyList<McpDetectedCandidate>>(result);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"detect failed: {ex.GetType().Name}: {ex.Message}", ex);
        }
    }
}
