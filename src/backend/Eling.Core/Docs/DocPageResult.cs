namespace Eling.Core.Docs;

/// <summary>A server-sorted page of docs plus the unpaged total.</summary>
public sealed record DocPageResult(
    IReadOnlyList<DocListItem> Items,
    int Total);
