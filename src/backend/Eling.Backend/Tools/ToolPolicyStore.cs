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
/// for inspection.
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

    public ToolPolicyStore(UserScope userScope, ILogger<ToolPolicyStore> logger)
    {
        ArgumentNullException.ThrowIfNull(userScope);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _filePath = Path.Combine(userScope.ConfigDirectory, FileName);
        Load();
        EnsureTemplate();
    }

    public static bool IsProtected(string? toolName)
        => !string.IsNullOrWhiteSpace(toolName) && ProtectedTools.Contains(toolName.Trim());

    public IReadOnlySet<string> GetDisabledTools()
    {
        lock (_gate)
        {
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
            return _config.DisabledTools.Contains(toolName.Trim(), StringComparer.OrdinalIgnoreCase);
        }
    }

    public ToolPolicyConfig GetSnapshot()
    {
        lock (_gate)
        {
            return _config with { DisabledTools = new List<string>(_config.DisabledTools) };
        }
    }

    public ToolPolicyConfig DisableTools(IEnumerable<string> toolNames)
    {
        ArgumentNullException.ThrowIfNull(toolNames);

        lock (_gate)
        {
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

            var config = JsonSerializer.Deserialize(
                File.ReadAllText(_filePath),
                ToolPolicyJsonContext.Default.ToolPolicyConfig);

            if (config is null)
            {
                return;
            }

            var sanitized = config.DisabledTools
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Where(name => !IsProtected(name))
                .Select(name => name.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            _config = config with { DisabledTools = sanitized };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read tool policy from {Path}", _filePath);
        }
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
}
