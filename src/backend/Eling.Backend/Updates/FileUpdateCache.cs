using System.Text.Json;
using Eling.Core.Scope;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Updates;

/// <summary>
/// Persisted update-check snapshot at <c>&lt;user-scope&gt;/config/update-check.json</c>.
/// Follows the config-store conventions: snake_case indented JSON, atomic
/// temp-then-move writes, and a corrupt or missing file degrading to "unknown"
/// instead of ever throwing.
/// </summary>
public sealed class FileUpdateCache
{
    private const string FileName = "update-check.json";

    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly ILogger<FileUpdateCache> _logger;
    private UpdateStatus? _status;

    public FileUpdateCache(UserScope userScope, ILogger<FileUpdateCache> logger)
    {
        ArgumentNullException.ThrowIfNull(userScope);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _filePath = Path.Combine(userScope.ConfigDirectory, FileName);
        Load();
    }

    /// <summary>Last persisted status, or <c>null</c> when never checked.</summary>
    public UpdateStatus? GetStatus()
    {
        lock (_gate)
        {
            return _status;
        }
    }

    /// <summary>Persists the status to memory and to disk.</summary>
    public void SaveStatus(UpdateStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        lock (_gate)
        {
            _status = status;
            Write();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            var status = JsonSerializer.Deserialize<UpdateStatus>(
                File.ReadAllText(_filePath),
                FileUpdateJsonContext.Default.UpdateStatus);
            if (status is not null)
            {
                _status = status;
            }
        }
        catch (Exception ex)
        {
            // A corrupt cache must not take the backend down: behave as if
            // never checked, which simply delays the offer until next time.
            _logger.LogWarning(ex, "Failed to read update cache from {Path}", _filePath);
        }
    }

    private void Write()
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = _filePath + ".tmp";
            File.WriteAllText(
                tempPath,
                JsonSerializer.Serialize(_status, FileUpdateJsonContext.Default.UpdateStatus));
            File.Move(tempPath, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write update cache to {Path}", _filePath);
        }
    }
}
