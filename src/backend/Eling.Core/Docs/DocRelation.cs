namespace Eling.Core.Docs;

/// <summary>
/// A relation between two documents (spec ↔ plan, etc.).
/// </summary>
public sealed class DocRelation
{
    public DocId SourceId { get; }
    public DocId TargetId { get; }
    public RelationKind Kind { get; }

    public DocRelation(DocId sourceId, DocId targetId, RelationKind kind)
    {
        SourceId = sourceId;
        TargetId = targetId;
        Kind = kind;
    }
}