using System.Text.Json;
using Microsoft.Extensions.Logging;

using Eling.Core.Serialization;

namespace Eling.Backend.Agent.Services;

public sealed class WorkspaceRegistry
{
    private const string FileName = "agent-workspaces.json";

    private static System.Text.Json.JsonSerializerOptions JsonOptions => JsonDefaults.Shared;

    private readonly string _filePath;
    private readonly ILogger<WorkspaceRegistry> _logger;
    private readonly object _gate = new();
    private List<string> _roots = [];

    public WorkspaceRegistry(string filePath, ILogger<WorkspaceRegistry> logger, string? legacyFilePath = null)
    {
        _filePath = filePath;
        _logger = logger;
        if (!string.IsNullOrWhiteSpace(legacyFilePath))
        {
            StoreMigration.MoveFileIfNeeded(_filePath, legacyFilePath, _logger, "workspace registry");
        }

        Load();
    }

    public IReadOnlyList<string> List()
    {
        lock (_gate)
        {
            return new List<string>(_roots);
        }
    }

    public void Add(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            throw new ArgumentException($"Workspace directory does not exist: {path}", nameof(path));
        }

        var full = Path.GetFullPath(path);
        lock (_gate)
        {
            if (!_roots.Contains(full, StringComparer.OrdinalIgnoreCase))
            {
                _roots.Add(full);
                Save();
            }
        }
    }

    public void Remove(string path)
    {
        var full = Path.GetFullPath(path);
        lock (_gate)
        {
            _roots.RemoveAll(r => string.Equals(r, full, StringComparison.OrdinalIgnoreCase));
            Save();
        }
    }

    public string Resolve(string root, string? relativePath)
    {
        var rootFull = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(root, relativePath ?? string.Empty));
        if (!full.Equals(rootFull, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Path escapes workspace root.");
        }

        return full;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            var json = File.ReadAllText(_filePath);
            var roots = JsonSerializer.Deserialize<List<string>>(json, JsonOptions);
            _roots = roots ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load workspace registry from {Path}", _filePath);
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(_filePath, JsonSerializer.Serialize(_roots, JsonOptions));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save workspace registry to {Path}", _filePath);
        }
    }
}
