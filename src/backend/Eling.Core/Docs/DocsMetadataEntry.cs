namespace Eling.Core.Docs;

/// <summary>Metadata for one doc, keyed by path in the committed JSON file.</summary>
public sealed class DocsMetadataEntry
{
    public string Id { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string WorkspaceId { get; set; } = string.Empty;
    public DocKind Kind { get; set; }
    public string Topic { get; set; } = string.Empty;
    public int Status { get; set; }
    public int Order { get; set; }
    public bool Archived { get; set; }
    public string? SupersededBy { get; set; }
    public string? FileHash { get; set; }
    public List<DocRelationMeta> Relations { get; set; } = [];
    public Dictionary<string, DocSectionAnnotation> Annotations { get; set; } = [];
}
