using System.Text.Json;
using Eling.Core.Scope;
using Eling.Core.Shell;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Shell;

/// <summary>
/// Reads and writes the global <c>shell_execute</c> configuration from
/// <c>&lt;user-scope&gt;/config/shell-config.json</c>.
/// </summary>
/// <remarks>
/// Thread-safe via a private gate. Mutations are stamped with <c>UpdatedAt</c>
/// in code, persisted through a temp file followed by a move/replace, and swap
/// the in-memory snapshot only after the write succeeds. A corrupt file
/// degrades to the last good snapshot — the template defaults on first load —
/// and is left untouched for inspection. Because the dashboard host and each
/// MCP stdio host are separate processes sharing this one file, every read and
/// mutation goes through <c>RefreshIfStale</c>, which reloads the file whenever
/// another process changed it. Rules whose <c>Path</c>/<c>Pattern</c> is blank
/// are dropped on read and on mutation: an empty pattern matches every command.
/// </remarks>
public sealed class ShellConfigStore
{
    private const string FileName = "shell-config.json";

    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly ILogger<ShellConfigStore> _logger;

    private ShellConfig _config = new();

    /// <summary>Stamp of the file as this store last saw it; null means "not yet stamped".</summary>
    private FileStamp? _stamp;

    public ShellConfigStore(UserScope userScope, ILogger<ShellConfigStore> logger)
    {
        ArgumentNullException.ThrowIfNull(userScope);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _filePath = Path.Combine(userScope.ConfigDirectory, FileName);
        Load();
        EnsureTemplate();
        _stamp = ReadStamp();
    }

    public ShellConfig GetConfig()
    {
        lock (_gate)
        {
            RefreshIfStale();
            return GetSnapshotLocked();
        }
    }

    /// <summary>
    /// Applies <paramref name="mutate"/> to the current configuration, stamps
    /// it, and persists it atomically. The snapshot is swapped only after the
    /// write succeeds; a failed write is thrown so callers never return a
    /// stale snapshot as if the mutation had landed.
    /// </summary>
    public ShellConfig Update(Func<ShellConfig, ShellConfig> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        lock (_gate)
        {
            RefreshIfStale();

            var mutated = mutate(GetSnapshotLocked());
            var next = Sanitize(mutated with { UpdatedAt = DateTimeOffset.UtcNow });

            Write(next);
            _config = next;
            _stamp = ReadStamp();

            return GetSnapshotLocked();
        }
    }

    /// <summary>
    /// Switches one allow-list rule on or off, leaving every other rule as it
    /// is. The path is compared case-insensitively because that is how the tool
    /// compares it when it validates a working directory.
    /// </summary>
    public ShellConfig ToggleAllowedDirectory(string path, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var target = path.Trim();

        return Update(config => config with
        {
            AllowedDirectories = ToggleRules(config.AllowedDirectories, rule => rule.Path, target, enabled),
        });
    }

    /// <summary>
    /// Switches one blocked-command rule on or off, leaving every other rule as
    /// it is. The pattern is compared case-insensitively because the tool
    /// matches it against the command line the same way.
    /// </summary>
    public ShellConfig ToggleBlockedCommand(string pattern, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        var target = pattern.Trim();

        return Update(config => config with
        {
            BlockedCommands = ToggleRules(config.BlockedCommands, rule => rule.Pattern, target, enabled),
        });
    }

    private static List<T> ToggleRules<T>(List<T> rules, Func<T, string> key, string target, bool enabled)
        where T : ShellRuleBase
        => rules
            .Select(rule => string.Equals(key(rule), target, StringComparison.OrdinalIgnoreCase)
                ? rule with { Enabled = enabled, UpdatedAt = DateTimeOffset.UtcNow }
                : rule)
            .ToList();

    private ShellConfig GetSnapshotLocked()
        => _config with
        {
            AllowedDirectories = [.. _config.AllowedDirectories],
            BlockedCommands = [.. _config.BlockedCommands],
        };

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
            _logger.LogWarning(ex, "Failed to read shell configuration from {Path}", _filePath);
        }
    }

    /// <summary>
    /// Reloads the configuration file when it changed since this store last
    /// read or wrote it. Every process sharing the file — the dashboard host and
    /// each MCP stdio host — holds its own store singleton, so without this a
    /// change made through one process stays invisible to the others and can be
    /// silently reverted by a later mutation committed from a stale snapshot.
    /// Must be called under <see cref="_gate"/>: from every read path and before
    /// every mutation.
    /// </summary>
    private void RefreshIfStale()
    {
        var stamp = ReadStamp();
        if (stamp is null || stamp == _stamp)
        {
            // Missing file or no change: keep the last good snapshot. A deleted
            // file must not silently switch the tool back to the template.
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
            _logger.LogWarning(ex, "Could not read shell configuration from {Path}; keeping the last good snapshot", _filePath);
        }
        catch (JsonException ex)
        {
            // The file was read but is corrupt: adopt the stamp so every read
            // does not re-log the warning, and keep the last good snapshot. A
            // repaired file changes the stamp and reloads normally.
            _logger.LogWarning(ex, "Shell configuration at {Path} is corrupt; keeping the last good snapshot", _filePath);
            _stamp = stamp;
        }
    }

    /// <summary>
    /// Stamp of the configuration file used to detect writes from other
    /// processes, or null when the file cannot be inspected.
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
            _logger.LogDebug(ex, "Could not read shell configuration stamp from {Path}; treating as absent", _filePath);
            return null;
        }
    }

    /// <summary>
    /// Reads and sanitizes the configuration file. Throws on unreadable or
    /// corrupt content; callers decide how to degrade.
    /// </summary>
    private ShellConfig ReadConfig()
    {
        var config = JsonSerializer.Deserialize(
                File.ReadAllText(_filePath),
                ShellJsonContext.Default.ShellConfig)
            ?? new ShellConfig();

        return Sanitize(config);
    }

    /// <summary>
    /// Drops rules that are inert or dangerous — a blank path can never match a
    /// directory, and a blank pattern matches every command — and trims the
    /// values that are kept. Runs on every read and on every mutation, so no
    /// consumer ever sees such a rule. A null entry arrives here because the
    /// deserializer keeps <c>null</c> list items rather than rejecting them.
    /// </summary>
    private ShellConfig Sanitize(ShellConfig config)
    {
        var directories = new List<AllowedDirectoryRule>(config.AllowedDirectories.Count);
        foreach (var rule in config.AllowedDirectories)
        {
            if (rule is null || string.IsNullOrWhiteSpace(rule.Path))
            {
                _logger.LogWarning("Dropping allow-list rule with a blank path from {Path}", _filePath);
                continue;
            }

            directories.Add(rule with { Path = rule.Path.Trim() });
        }

        var commands = new List<BlockedCommandRule>(config.BlockedCommands.Count);
        foreach (var rule in config.BlockedCommands)
        {
            if (rule is null || string.IsNullOrWhiteSpace(rule.Pattern))
            {
                _logger.LogWarning("Dropping blocked-command rule with a blank pattern from {Path}", _filePath);
                continue;
            }

            commands.Add(rule with { Pattern = rule.Pattern.Trim() });
        }

        return config with { AllowedDirectories = directories, BlockedCommands = commands };
    }

    private void EnsureTemplate()
    {
        if (File.Exists(_filePath))
        {
            return;
        }

        var template = new ShellConfig();

        try
        {
            Write(template);
            _config = template;
            _logger.LogInformation("Created shell configuration template at {Path}", _filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write shell configuration template to {Path}", _filePath);
        }
    }

    private void Write(ShellConfig config)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var payload = JsonSerializer.Serialize(config, ShellJsonContext.Default.ShellConfig);
        var tempPath = _filePath + ".tmp";
        File.WriteAllText(tempPath, payload);
        File.Move(tempPath, _filePath, overwrite: true);
    }

    /// <summary>
    /// Change detection for the configuration file: last write time plus
    /// length, so two writes landing in the same timestamp tick still compare
    /// unequal.
    /// </summary>
    private sealed record FileStamp(
        DateTime LastWriteTimeUtc,
        long Length);
}
