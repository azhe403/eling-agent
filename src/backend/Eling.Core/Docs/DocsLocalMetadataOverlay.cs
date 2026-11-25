namespace Eling.Core.Docs;

using System.Text.Json;

/// <summary>
/// Metadata for doc files outside the workspace root. Stored under .eling/
/// because the committed JSON must never contain machine-specific paths.
/// </summary>
public sealed class DocsLocalMetadataOverlay
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _path;

    public DocsLocalMetadataOverlay(string workspaceRoot)
    {
        var dir = Path.Combine(workspaceRoot, ".eling");
        _path = Path.Combine(dir, "docs-local-metadata.json");
    }

    public async Task<Dictionary<string, DocsMetadataEntry>> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path)) return new Dictionary<string, DocsMetadataEntry>(StringComparer.Ordinal);
        try
        {
            var json = await File.ReadAllTextAsync(_path, ct);
            return JsonSerializer.Deserialize<Dictionary<string, DocsMetadataEntry>>(json, JsonOptions)
                   ?? new Dictionary<string, DocsMetadataEntry>(StringComparer.Ordinal);
        }
        catch (JsonException ex)
        {
            System.Diagnostics.Debug.WriteLine(ex.Message);
            return new Dictionary<string, DocsMetadataEntry>(StringComparer.Ordinal);
        }
    }

    public async Task SaveAsync(Dictionary<string, DocsMetadataEntry> entries, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var json = JsonSerializer.Serialize(entries, JsonOptions);
        await File.WriteAllTextAsync(_path, json, ct);
    }
}
