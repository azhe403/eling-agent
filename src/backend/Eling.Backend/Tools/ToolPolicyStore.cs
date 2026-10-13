using System.Text.Json;
using Eling.Core.Scope;
using Eling.Core.Tools;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Tools;

/// <summary>
/// Reads and writes the per-user tool enablement policy from
/// <c>&lt;user-scope&gt;/config/tools-policy.json</c>.
/// </summary>
/// <remarks>
/// Thread-safe via a private gate. Mutations generate <c>UpdatedAt</c> in code
/// at the end of the mutation, persist through a temp file followed by a
/// move/replace, and swap the in-memory snapshot only after the write
/// succeeds. A corrupt file degrades to "all enabled" and is left untouched
/// for inspection. Because the dashboard host and each MCP stdio host are
/// separate processes sharing this one file, reads and mutations go through
/// <c>RefreshIfStale</c>, which reloads the file whenever another process
/// changed it.
/// </remarks>
public sealed class ToolPolicyStore
{
    private const string FileName = "tools-policy.json";

    private static readonly HashSet<string> ProtectedTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "tools_policy",
        "memory_recall",
    };

    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly ILogger<ToolPolicyStore> _logger;

    private ToolPolicyConfig _config = new() { UpdatedAt = DateTimeOffset.UtcNow };

    /// <summary>Stamp of the file as this store last saw it; null means "not yet stamped".</summary>
    private FileStamp? _stamp;

    public ToolPolicyStore(UserScope userScope, ILogger<ToolPolicyStore> logger)
    {
        ArgumentNullException.ThrowIfNull(userScope);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _filePath = Path.Combine(userScope.ConfigDirectory, FileName);
        Load();
        EnsureTemplate();
        _stamp = ReadStamp();
    }

    public static bool IsProtected(string? toolName)
        => !string.IsNullOrWhiteSpace(toolName) && ProtectedTools.Contains(toolName.Trim());

    public IReadOnlySet<string> GetDisabledTools()
    {
        lock (_gate)
        {
            RefreshIfStale();
            return new HashSet<string>(_config.DisabledTools, StringComparer.OrdinalIgnoreCase);
        }
    }

    public bool IsToolDisabled(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return false;
        }

        lock (_gate)
        {
            RefreshIfStale();
            return _config.DisabledTools.Contains(toolName.Trim(), StringComparer.OrdinalIgnoreCase);
        }
    }

    public ToolPolicyConfig GetSnapshot()
    {
        lock (_gate)
        {
            RefreshIfStale();
            return _config with { DisabledTools = new List<string>(_config.DisabledTools) };
        }
    }

    public ToolPolicyConfig DisableTools(IEnumerable<string> toolNames)
    {
        ArgumentNullException.ThrowIfNull(toolNames);

        lock (_gate)
        {
            RefreshIfStale();
            var next = new HashSet<string>(_config.DisabledTools, StringComparer.OrdinalIgnoreCase);
            foreach (var name in toolNames)
            {
                var normalized = Normalize(name);
                if (normalized is null || IsProtected(normalized))
                {
                    continue;
                }

                next.Add(normalized);
            }

            return Commit(next);
        }
    }

    public ToolPolicyConfig EnableTools(IEnumerable<string> toolNames)
    {
        ArgumentNullException.ThrowIfNull(toolNames);

        lock (_gate)
        {
            RefreshIfStale();
            var next = new HashSet<string>(_config.DisabledTools, StringComparer.OrdinalIgnoreCase);
            foreach (var name in toolNames)
            {
                var normalized = Normalize(name);
                if (normalized is null)
                {
                    continue;
                }

                next.Remove(normalized);
            }

            return Commit(next);
        }
    }

    public ToolPolicyConfig Reset()
    {
        lock (_gate)
        {
            RefreshIfStale();
            return Commit(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }
    }

    private ToolPolicyConfig Commit(HashSet<string> disabled)
    {
        var next = new ToolPolicyConfig
        {
            DisabledTools = disabled.OrderBy(name => name, StringComparer.Ordinal).ToList(),
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        try
        {
            Write(next);
            _config = next;
            _stamp = ReadStamp();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write tool policy to {Path}", _filePath);
        }

        return GetSnapshotLocked();
    }

    private ToolPolicyConfig GetSnapshotLocked()
        => _config with { DisabledTools = new List<string>(_config.DisabledTools) };

    private static string? Normalize(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return null;
        }

        return toolName.Trim().ToLowerInvariant();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            _config = ReadConfig();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read tool policy from {Path}", _filePath);
        }
    }

    /// <summary>
    /// Reloads the policy file when it changed since this store last read or
    /// wrote it. Every process sharing the file — the dashboard host and each
    /// MCP stdio host — holds its own store singleton, so without this a
    /// change made through one process stays invisible to the others and can
    /// be silently reverted by a later mutation committed from a stale
    /// snapshot. Must be called under <see cref="_gate"/>: from every read
    /// path and before every mutation.
    /// </summary>
    private void RefreshIfStale()
    {
        var stamp = ReadStamp();
        if (stamp is null || stamp == _stamp)
        {
            // Missing file or no change: keep the last good snapshot. A deleted
            // file must not silently re-enable every tool.
            return;
        }

        try
        {
            _config = ReadConfig();
            _stamp = stamp;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Transient read failure: keep the stamp so the next call retries.
            _logger.LogWarning(ex, "Could not read tool policy from {Path}; keeping the last good snapshot", _filePath);
        }
        catch (JsonException ex)
        {
            // The file was read but is corrupt: adopt the stamp so every read
            // does not re-log the warning, and keep the last good snapshot. A
            // repaired file changes the stamp and reloads normally.
            _logger.LogWarning(ex, "Tool policy at {Path} is corrupt; keeping the last good snapshot", _filePath);
            _stamp = stamp;
        }
    }

    /// <summary>
    /// Stamp of the policy file used to detect writes from other processes,
    /// or null when the file cannot be inspected.
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
            return null;
        }
    }

    /// <summary>
    /// Reads and sanitizes the policy file. Throws on unreadable or corrupt
    /// content; callers decide how to degrade.
    /// </summary>
    private ToolPolicyConfig ReadConfig()
    {
        var config = JsonSerializer.Deserialize(
                File.ReadAllText(_filePath),
                ToolPolicyJsonContext.Default.ToolPolicyConfig)
            ?? new ToolPolicyConfig();

        var sanitized = config.DisabledTools
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Where(name => !IsProtected(name))
            .Select(name => name.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        return config with { DisabledTools = sanitized };
    }

    private void EnsureTemplate()
    {
        if (File.Exists(_filePath))
        {
            return;
        }

        var template = new ToolPolicyConfig
        {
            DisabledTools = new List<string>(),
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        try
        {
            Write(template);
            _config = template;
            _logger.LogInformation("Created tool policy template at {Path}", _filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write tool policy template to {Path}", _filePath);
        }
    }

    private void Write(ToolPolicyConfig config)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var payload = JsonSerializer.Serialize(config, ToolPolicyJsonContext.Default.ToolPolicyConfig);
        var tempPath = _filePath + ".tmp";
        File.WriteAllText(tempPath, payload);
        File.Move(tempPath, _filePath, overwrite: true);
    }

    /// <summary>
    /// Change detection for the policy file: last write time plus length, so
    /// two writes landing in the same timestamp tick still compare unequal.
    /// </summary>
    private sealed record FileStamp(
        DateTime LastWriteTimeUtc,
        long Length);
}
