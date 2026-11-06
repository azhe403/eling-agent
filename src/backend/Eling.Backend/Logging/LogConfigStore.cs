using System.Text.Json;
using Eling.Core.Logging;
using Eling.Core.Scope;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Logging;

/// <summary>
/// Reads and writes the per-user log format setting from
/// <c>&lt;user-scope&gt;/config/log.json</c>.
/// </summary>
/// <remarks>
/// Mirrors <c>ToolPolicyStore</c>: thread-safe via a private gate; mutations
/// write through a temp file followed by a move/replace and swap the
/// in-memory snapshot only after the write succeeds. A corrupt or unknown
/// file degrades to <see cref="LogFormat.Text"/> and is left untouched for
/// inspection. Because the dashboard host and each MCP stdio host are
/// separate processes sharing this one file, reads and mutations go through
/// <c>RefreshIfStale</c>, which reloads the file whenever another process
/// changed it.
/// </remarks>
public sealed class LogConfigStore
{
    private const string FileName = "log.json";

    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly ILogger<LogConfigStore> _logger;

    private LogFormat _format = LogFormat.Text;

    /// <summary>Stamp of the file as this store last saw it; null means "not yet stamped".</summary>
    private FileStamp? _stamp;

    public LogConfigStore(UserScope userScope, ILogger<LogConfigStore> logger)
    {
        ArgumentNullException.ThrowIfNull(userScope);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _filePath = Path.Combine(userScope.ConfigDirectory, FileName);
        Load();
        EnsureTemplate();
        _stamp = ReadStamp();
    }

    /// <summary>
    /// The log format this store last observed, re-reading the file when
    /// another process changed it.
    /// </summary>
    public LogFormat GetFormat()
    {
        lock (_gate)
        {
            RefreshIfStale();
            return _format;
        }
    }

    /// <summary>
    /// A point-in-time snapshot of the current format.
    /// </summary>
    public LogFormat Format
    {
        get
        {
            lock (_gate)
            {
                RefreshIfStale();
                return _format;
            }
        }
    }

    /// <summary>
    /// Persists <paramref name="format"/> and returns it. A failed write keeps
    /// the last good in-memory value and is logged.
    /// </summary>
    public LogFormat UpdateFormat(LogFormat format)
    {
        lock (_gate)
        {
            RefreshIfStale();

            var dto = new LogConfigDto(format.ToString().ToLowerInvariant());

            try
            {
                Write(dto);
                _format = format;
                _stamp = ReadStamp();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write log config to {Path}", _filePath);
            }

            return _format;
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

            _format = ResolveRead();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read log config from {Path}", _filePath);
        }
    }

    private void EnsureTemplate()
    {
        if (File.Exists(_filePath))
        {
            return;
        }

        var template = new LogConfigDto(LogFormat.Text.ToString().ToLowerInvariant());

        try
        {
            Write(template);
            _logger.LogInformation("Created log config template at {Path}", _filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write log config template to {Path}", _filePath);
        }
    }

    /// <summary>
    /// Reloads the log config file when it changed since this store last read or
    /// wrote it. Every process sharing the file — the dashboard host and each
    /// MCP stdio host — holds its own store singleton, so without this a
    /// change made through one process stays invisible to the others and can
    /// be silently reverted by a later write. Must be called under
    /// <see cref="_gate"/>: from every read path and before every mutation.
    /// </summary>
    private void RefreshIfStale()
    {
        var stamp = ReadStamp();
        if (stamp is null || stamp == _stamp)
        {
            // Missing file or no change: keep the last good snapshot. A deleted
            // file must not silently reset the format.
            return;
        }

        try
        {
            _format = ResolveRead();
            _stamp = stamp;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Transient read failure: keep the stamp so the next call retries.
            _logger.LogWarning(ex, "Could not read log config from {Path}; keeping the last good snapshot", _filePath);
        }
        catch (JsonException ex)
        {
            // The file was read but is corrupt: adopt the stamp so every read
            // does not re-log the warning, and keep the last good snapshot. A
            // repaired file changes the stamp and reloads normally.
            _logger.LogWarning(ex, "Log config at {Path} is corrupt; keeping the last good snapshot", _filePath);
            _stamp = stamp;
        }
    }

    /// <summary>
    /// Reads and resolves the config file. Throws on unreadable or corrupt
    /// content; callers decide how to degrade.
    /// </summary>
    private LogFormat ResolveRead()
    {
        var dto = JsonSerializer.Deserialize(
                File.ReadAllText(_filePath),
                LogConfigJsonContext.Default.LogConfigDto)
            ?? new LogConfigDto(null);

        return LogFormatExtensions.Resolve(dto.Format);
    }

    private void Write(LogConfigDto dto)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var payload = JsonSerializer.Serialize(dto, LogConfigJsonContext.Default.LogConfigDto);
        var tempPath = _filePath + ".tmp";
        File.WriteAllText(tempPath, payload);
        File.Move(tempPath, _filePath, overwrite: true);
    }

    /// <summary>
    /// Change detection for the config file: last write time plus length, so
    /// two writes landing in the same timestamp tick still compare unequal.
    /// </summary>
    private FileStamp? ReadStamp()
    {
        try
        {
            var info = new FileInfo(_filePath);
            return info.Exists ? new FileStamp(info.LastWriteTimeUtc, info.Length) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not read log config stamp from {Path}; treating as absent", _filePath);
            return null;
        }
    }

    private sealed record FileStamp(
        DateTime LastWriteTimeUtc,
        long Length);
}
