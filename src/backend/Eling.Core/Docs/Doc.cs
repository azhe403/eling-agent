namespace Eling.Core.Docs;

/// <summary>
/// A document entity: content lives in a Markdown file, metadata in the
/// workspace's docs-metadata.json. Ownership and origin were removed.
/// </summary>
public sealed class Doc
{
    public DocId Id { get; }
    public string WorkspaceId { get; }
    public string Path { get; }
    public DocKind Kind { get; set; }
    public string Title { get; set; }
    public DocStatusValue Status { get; set; }
    public string Topic { get; set; }
    public int Order { get; set; }
    public bool Archived { get; set; }
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
        string workspaceId,
        string path,
        DocKind kind,
        string title,
        DocStatusValue status,
        string topic,
        int order,
        bool archived,
        string? supersededBy,
        int stepsTotal,
        int stepsDone,
        string fileHash,
        DateTimeOffset mTime,
        DateTimeOffset? indexedAt = null,
        bool frontMatterError = false,
        string? frontMatterErrorMessage = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);

        Id = id;
        WorkspaceId = workspaceId;
        Path = path;
        Kind = kind;
        Title = title;
        Status = status;
        Topic = topic;
        Order = order;
        Archived = archived;
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
}
