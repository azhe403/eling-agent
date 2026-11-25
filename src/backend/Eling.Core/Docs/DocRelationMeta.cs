namespace Eling.Core.Docs;

/// <summary>A relation declared in committed metadata.</summary>
public sealed class DocRelationMeta
{
    public string TargetPath { get; set; } = string.Empty;
    public RelationKind Kind { get; set; }
}
