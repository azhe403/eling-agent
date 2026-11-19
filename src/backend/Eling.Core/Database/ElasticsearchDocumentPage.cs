namespace Eling.Core.Database;

public sealed record ElasticsearchDocumentPage(
    long Total,
    int From,
    int Size,
    long TookMs,
    IReadOnlyList<ElasticsearchHit> Hits);
