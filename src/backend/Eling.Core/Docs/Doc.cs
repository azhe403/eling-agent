namespace Eling.Core.Docs;

/// <summary>
/// A document entity. The canonical representation in the index.
/// </summary>
public sealed class Doc
{
    public DocId Id { get; }
    public DocOwnership Ownership { get; }
    public DocOrigin Origin { get; }
    public string RelPath { get; }
    public DocKind Kind { get; set; }
    public string Title { get; set; }
    public DocStatusValue Status { get; set; }
    public string Topic { get; set; }
    public int Order { get; set; }
    public string? SupersededBy { get; set; }
    public int StepsTotal { get; set; }
    public int StepsDone { get; set; }
    public string FileHash { get; set; }
    public DateTimeOffset MTime { get; set; }
    public DateTimeOffset IndexedAt { get; set; }
    public bool FrontMatterError { get; set; }
    public string? FrontMatterErrorMessage { get; set; }

    public Doc(
        DocId id,
        DocOwnership ownership,
        DocOrigin origin,
        string relPath,
        DocKind kind,
        string title,
        DocStatusValue status,
        string topic,
        int order,
        string? supersededBy,
        int stepsTotal,
        int stepsDone,
        string fileHash,
        DateTimeOffset mTime,
        DateTimeOffset? indexedAt = null,
        bool frontMatterError = false,
        string? frontMatterErrorMessage = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);

        Id = id;
        Ownership = ownership;
        Origin = origin;
        RelPath = relPath;
        Kind = kind;
        Title = title;
        Status = status;
        Topic = topic;
        Order = order;
        SupersededBy = supersededBy;
        StepsTotal = stepsTotal;
        StepsDone = stepsDone;
        FileHash = fileHash;
        MTime = mTime;
        IndexedAt = indexedAt ?? DateTimeOffset.UtcNow;
        FrontMatterError = frontMatterError;
        FrontMatterErrorMessage = frontMatterErrorMessage;
    }

    public double Progress => StepsTotal > 0 ? (double)StepsDone / StepsTotal : 0.0;

    public bool IsExternal => Ownership == DocOwnership.External;
    public bool IsManaged => Ownership == DocOwnership.Managed;
}