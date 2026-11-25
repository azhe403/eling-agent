namespace Eling.Core.Docs;

/// <summary>
/// The committed docs-metadata.json at a workspace root. Path-keyed so one
/// file can never appear twice; the ULID lives inside the entry.
/// </summary>
public sealed class DocsMetadataDocument
{
    public int Version { get; set; } = 1;
    public CoveragePolicy Coverage { get; set; } = new();
    public Dictionary<string, DocsMetadataEntry> Docs { get; set; } = new(StringComparer.Ordinal);
}
