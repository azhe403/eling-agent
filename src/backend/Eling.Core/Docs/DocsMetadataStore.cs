namespace Eling.Core.Docs;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>
/// Canonical metadata store: one committed JSON file per workspace, plus a
/// local overlay for paths outside the workspace so machine paths stay out of git.
/// </summary>
public sealed class DocsMetadataStore
{
    public const string FileName = "docs-metadata.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static string ResolveWorkspaceId(string workspaceRoot)
    {
        var normalized = Path.GetFullPath(workspaceRoot).Replace('\\', '/').TrimEnd('/');
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public async Task<DocsMetadataDocument> LoadAsync(string workspaceRoot, CancellationToken ct = default)
        => (await LoadWithFindingsAsync(workspaceRoot, ct)).Document;

    public async Task<MetadataLoadResult> LoadWithFindingsAsync(
        string workspaceRoot, CancellationToken ct = default)
    {
        var path = Path.Combine(workspaceRoot, FileName);
        var findings = new List<string>();
        if (!File.Exists(path)) return new MetadataLoadResult(new DocsMetadataDocument(), findings);

        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            using var rootJson = JsonDocument.Parse(json);
            var document = new DocsMetadataDocument();
            if (TryGet(rootJson.RootElement, "version", out var version) && version.TryGetInt32(out var v))
                document.Version = v;
            if (TryGet(rootJson.RootElement, "coverage", out var coverage))
                document.Coverage = JsonSerializer.Deserialize<CoveragePolicy>(coverage.GetRawText(), JsonOptions) ?? new CoveragePolicy();
            if (TryGet(rootJson.RootElement, "docs", out var docs) && docs.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in docs.EnumerateObject())
                {
                    try
                    {
                        var entry = JsonSerializer.Deserialize<DocsMetadataEntry>(property.Value.GetRawText(), JsonOptions);
                        if (entry is not null) document.Docs[property.Name] = entry;
                    }
                    catch (JsonException ex)
                    {
                        findings.Add($"Malformed metadata entry {property.Name}: {ex.Message}");
                    }
                }
            }
            return new MetadataLoadResult(document, findings);
        }
        catch (JsonException ex)
        {
            findings.Add($"Malformed {FileName}: {ex.Message}");
            return new MetadataLoadResult(new DocsMetadataDocument(), findings);
        }
    }

    private static bool TryGet(System.Text.Json.JsonElement element, string name, out System.Text.Json.JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    public async Task SaveAsync(string workspaceRoot, DocsMetadataDocument document, CancellationToken ct = default)
    {
        var committed = new DocsMetadataDocument
        {
            Version = document.Version,
            Coverage = document.Coverage
        };
        var outside = new Dictionary<string, DocsMetadataEntry>(StringComparer.Ordinal);
        foreach (var pair in document.Docs)
        {
            var path = pair.Key;
            var entry = pair.Value;
            if (path.StartsWith("abs:", StringComparison.OrdinalIgnoreCase)) outside[path] = entry;
            else committed.Docs[path] = entry;
        }

        var json = JsonSerializer.Serialize(committed, JsonOptions);
        await File.WriteAllTextAsync(Path.Combine(workspaceRoot, FileName), json, ct);

        if (outside.Count > 0)
        {
            var overlay = new DocsLocalMetadataOverlay(workspaceRoot);
            var existing = await overlay.LoadAsync(ct);
            foreach (var pair in outside) existing[pair.Key] = pair.Value;
            await overlay.SaveAsync(existing, ct);
        }
    }

    /// <summary>Returns the existing entry for a path, or a new one with a fresh ULID.</summary>
    public DocsMetadataEntry GetOrCreate(DocsMetadataDocument document, string path, string workspaceId)
    {
        if (document.Docs.TryGetValue(path, out var existing) && existing is not null) return existing;
        var entry = new DocsMetadataEntry
        {
            Id = DocId.NewId().Value,
            Path = path,
            WorkspaceId = workspaceId
        };
        document.Docs[path] = entry;
        return entry;
    }
}
