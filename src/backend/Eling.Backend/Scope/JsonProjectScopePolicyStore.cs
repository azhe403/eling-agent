using System.Text.Json;
using System.Text.Json.Serialization;
using Eling.Core.Scope;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Scope;

/// <summary>
/// JSON-backed <see cref="IProjectScopePolicyStore"/>. Persists
/// <c>&lt;user-scope&gt;/config/project-policy.json</c> atomically using async
/// file I/O (matching the async storage engine), preserves <c>version</c> and any
/// unknown keys, and owns all timestamp bookkeeping: <c>createdAt</c> is written
/// once, <c>updatedAt</c> only moves on an effective change, and an idempotent
/// mutation never rewrites the file.
/// </summary>
public sealed class JsonProjectScopePolicyStore : IProjectScopePolicyStore
{
    private const string FileName = "project-policy.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _configDirectory;
    private readonly string _path;
    private readonly string _home;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ILogger<JsonProjectScopePolicyStore>? _logger;

    public JsonProjectScopePolicyStore(
        UserScope userScope,
        Func<DateTimeOffset>? clock = null,
        string? userHomeDirectory = null,
        ILogger<JsonProjectScopePolicyStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(userScope);
        _configDirectory = userScope.ConfigDirectory;
        _path = Path.Combine(_configDirectory, FileName);
        _home = string.IsNullOrWhiteSpace(userHomeDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : Path.GetFullPath(userHomeDirectory);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _logger = logger;
    }

    public async Task<ProjectScopePolicy> LoadAsync() => ToPolicy(await LoadDocumentAsync());

    public async Task<ProjectScopePolicy> SetProjectAsync(string root, ProjectScopeDecision decision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var doc = await LoadDocumentAsync();
        var key = ProjectScopePolicy.NormalizeRoot(root);
        doc.Projects ??= new(StringComparer.OrdinalIgnoreCase);

        if (doc.Projects.TryGetValue(key, out var existing) && ParseDecision(existing.Decision) == decision)
        {
            return ToPolicy(doc);
        }

        var now = _clock();
        doc.CreatedAt ??= now;
        doc.UpdatedAt = now;
        doc.Projects[key] = new EntryDto
        {
            Decision = FormatDecision(decision),
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = now
        };

        await PersistAsync(doc);
        return ToPolicy(doc);
    }

    public async Task<ProjectScopePolicy> SetPatternAsync(string glob, ProjectScopeDecision decision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(glob);
        var doc = await LoadDocumentAsync();
        var normalized = NormalizeGlob(glob);
        doc.Patterns ??= [];

        var index = FindPatternIndex(doc, normalized);
        if (index >= 0 && ParseDecision(doc.Patterns[index].Decision) == decision)
        {
            return ToPolicy(doc);
        }

        var now = _clock();
        doc.CreatedAt ??= now;
        doc.UpdatedAt = now;

        if (index >= 0)
        {
            doc.Patterns[index].Glob = normalized;
            doc.Patterns[index].Decision = FormatDecision(decision);
            doc.Patterns[index].UpdatedAt = now;
        }
        else
        {
            doc.Patterns.Add(new PatternDto
            {
                Glob = normalized,
                Decision = FormatDecision(decision),
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        await PersistAsync(doc);
        return ToPolicy(doc);
    }

    public async Task<ProjectScopePolicy> SetDefaultAsync(ProjectScopeDecision decision)
    {
        var doc = await LoadDocumentAsync();
        if (ParseDecision(doc.Default) == decision)
        {
            return ToPolicy(doc);
        }

        var now = _clock();
        doc.CreatedAt ??= now;
        doc.UpdatedAt = now;
        doc.Default = FormatDecision(decision);

        await PersistAsync(doc);
        return ToPolicy(doc);
    }

    public async Task<ProjectScopePolicy> ClearProjectAsync(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var doc = await LoadDocumentAsync();
        var key = ProjectScopePolicy.NormalizeRoot(root);
        if (doc.Projects is null || !doc.Projects.Remove(key))
        {
            throw new KeyNotFoundException($"No project-scope policy entry for '{key}'.");
        }

        doc.UpdatedAt = _clock();
        await PersistAsync(doc);
        return ToPolicy(doc);
    }

    public async Task<ProjectScopePolicy> ClearPatternAsync(string glob)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(glob);
        var doc = await LoadDocumentAsync();
        var normalized = NormalizeGlob(glob);
        var index = FindPatternIndex(doc, normalized);
        if (index < 0)
        {
            throw new KeyNotFoundException($"No project-scope policy pattern matches '{normalized}'.");
        }

        doc.Patterns!.RemoveAt(index);
        doc.UpdatedAt = _clock();
        await PersistAsync(doc);
        return ToPolicy(doc);
    }

    private async Task<DocumentDto> LoadDocumentAsync()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new DocumentDto();
            }

            var json = await File.ReadAllTextAsync(_path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new DocumentDto();
            }

            var doc = JsonSerializer.Deserialize<DocumentDto>(json, Options) ?? new DocumentDto();
            return NormalizeDocument(doc);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "project-policy: could not read {Path}; using defaults", _path);
            return new DocumentDto();
        }
    }

    private DocumentDto NormalizeDocument(DocumentDto doc)
    {
        var projects = new Dictionary<string, EntryDto>(StringComparer.OrdinalIgnoreCase);
        if (doc.Projects is not null)
        {
            foreach (var pair in doc.Projects)
            {
                projects[ProjectScopePolicy.NormalizeRoot(pair.Key)] = pair.Value;
            }
        }

        doc.Projects = projects;

        if (doc.Patterns is not null)
        {
            foreach (var pattern in doc.Patterns)
            {
                if (!string.IsNullOrWhiteSpace(pattern.Glob))
                {
                    pattern.Glob = NormalizeGlob(pattern.Glob);
                }
            }
        }

        return doc;
    }

    private async Task PersistAsync(DocumentDto doc)
    {
        Directory.CreateDirectory(_configDirectory);
        var json = JsonSerializer.Serialize(doc, Options);
        var tmp = _path + ".tmp";
        await File.WriteAllTextAsync(tmp, json);
        File.Move(tmp, _path, overwrite: true);
    }

    private static int FindPatternIndex(DocumentDto doc, string normalizedGlob)
    {
        if (doc.Patterns is null)
        {
            return -1;
        }

        for (var i = 0; i < doc.Patterns.Count; i++)
        {
            var candidate = doc.Patterns[i].Glob;
            if (!string.IsNullOrWhiteSpace(candidate) &&
                string.Equals(candidate, normalizedGlob, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private string NormalizeGlob(string glob)
    {
        var normalized = glob.Trim().Replace('\\', '/');
        var home = _home.Replace('\\', '/').TrimEnd('/');
        if (normalized.Length == 0)
        {
            return normalized;
        }

        if (normalized == "~")
        {
            return home;
        }

        return normalized.StartsWith("~/", StringComparison.Ordinal)
            ? home + normalized[1..]
            : normalized;
    }

    private static ProjectScopeDecision ParseDecision(string? value)
        => string.Equals(value?.Trim(), "disabled", StringComparison.OrdinalIgnoreCase)
            ? ProjectScopeDecision.Disabled
            : ProjectScopeDecision.Ask;

    private static string FormatDecision(ProjectScopeDecision decision)
        => decision == ProjectScopeDecision.Disabled ? "disabled" : "ask";

    private static ProjectScopePolicy ToPolicy(DocumentDto doc)
    {
        var projects = new Dictionary<string, ProjectScopeEntry>(StringComparer.OrdinalIgnoreCase);
        if (doc.Projects is not null)
        {
            foreach (var pair in doc.Projects)
            {
                projects[pair.Key] = new ProjectScopeEntry(
                    ParseDecision(pair.Value.Decision), pair.Value.CreatedAt, pair.Value.UpdatedAt);
            }
        }

        var patterns = new List<ProjectScopePattern>();
        if (doc.Patterns is not null)
        {
            foreach (var pattern in doc.Patterns)
            {
                if (string.IsNullOrWhiteSpace(pattern.Glob))
                {
                    continue;
                }

                patterns.Add(new ProjectScopePattern(
                    pattern.Glob,
                    new ProjectScopeEntry(ParseDecision(pattern.Decision), pattern.CreatedAt, pattern.UpdatedAt)));
            }
        }

        return new ProjectScopePolicy(
            doc.CreatedAt, doc.UpdatedAt, ParseDecision(doc.Default), projects, patterns);
    }

    private sealed class DocumentDto
    {
        public int Version { get; set; } = 1;

        public DateTimeOffset? CreatedAt { get; set; }

        public DateTimeOffset? UpdatedAt { get; set; }

        public string? Default { get; set; }

        public Dictionary<string, EntryDto>? Projects { get; set; }

        public List<PatternDto>? Patterns { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }
    }

    private sealed class EntryDto
    {
        public string? Decision { get; set; }

        public DateTimeOffset? CreatedAt { get; set; }

        public DateTimeOffset? UpdatedAt { get; set; }
    }

    private sealed class PatternDto
    {
        public string? Glob { get; set; }

        public string? Decision { get; set; }

        public DateTimeOffset? CreatedAt { get; set; }

        public DateTimeOffset? UpdatedAt { get; set; }
    }
}
