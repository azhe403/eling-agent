using System.Text.Json;
using Eling.Core.Database;

namespace Eling.Backend.Tests;

/// <summary>
/// Test double for <see cref="IElasticsearchReadService"/>: captures the arguments
/// each call was made with and answers with canned values, or throws a configured
/// error. The structured filters are compiled with the real
/// <see cref="ElasticsearchFilterCompiler"/> so a bad op fails with a genuine
/// <see cref="ElasticsearchFilterException"/>, exactly as the production service
/// behaves (the compiler is static; no DI is needed for it).
/// </summary>
internal sealed class StubElasticsearchReadService : IElasticsearchReadService
{
    public ElasticsearchPingResult? PingResult { get; set; }
    public IReadOnlyList<ElasticsearchIndexInfo>? Indices { get; set; }
    public ElasticsearchIndexDetail? IndexDetail { get; set; }
    public ElasticsearchDocumentPage? DocumentPage { get; set; }
    public IReadOnlyList<ElasticsearchHistogramBucket>? HistogramBuckets { get; set; }
    public ElasticsearchSearchResult? SearchResult { get; set; }
    public long CountResult { get; set; }
    public Exception? Error { get; set; }

    public bool? IncludeSystem { get; private set; }
    public string? DocumentsIndex { get; private set; }
    public int? DocumentsFrom { get; private set; }
    public int? DocumentsSize { get; private set; }
    public string? DocumentsQ { get; private set; }
    public DateTimeOffset? DocumentsStart { get; private set; }
    public DateTimeOffset? DocumentsEnd { get; private set; }
    public string? DocumentsField { get; private set; }
    public IReadOnlyList<ElasticsearchFilterRow>? DocumentsFilters { get; private set; }
    public string? DocumentsLogic { get; private set; }

    public string? HistogramIndex { get; private set; }
    public string? HistogramField { get; private set; }
    public int? HistogramBucketsRequested { get; private set; }
    public string? HistogramQ { get; private set; }
    public DateTimeOffset? HistogramStart { get; private set; }
    public DateTimeOffset? HistogramEnd { get; private set; }

    public string? SearchIndex { get; private set; }
    public JsonElement? SearchQuery { get; private set; }
    public JsonElement? SearchSort { get; private set; }
    public JsonElement? SearchAggs { get; private set; }
    public int? SearchSize { get; private set; }
    public int? SearchFrom { get; private set; }
    public IReadOnlyList<string>? SearchSourceIncludes { get; private set; }

    public string? CountIndex { get; private set; }
    public JsonElement? CountQuery { get; private set; }

    public string? DetailIndex { get; private set; }

    public int PingCalls { get; private set; }

    public Task<ElasticsearchPingResult> PingAsync(
        DatabaseConnection connection,
        CancellationToken ct)
    {
        PingCalls++;
        if (Error is not null)
        {
            throw Error;
        }

        return Task.FromResult(PingResult ?? new ElasticsearchPingResult("8.19.2", 7));
    }

    public Task<IReadOnlyList<ElasticsearchIndexInfo>> ListIndicesAsync(
        DatabaseConnection connection,
        bool includeSystem,
        CancellationToken ct)
    {
        IncludeSystem = includeSystem;
        if (Error is not null)
        {
            throw Error;
        }

        return Task.FromResult(Indices
            ?? (IReadOnlyList<ElasticsearchIndexInfo>)new[]
            {
                new ElasticsearchIndexInfo("logs-app-2026", "green", "open", 124312, 48211320, 1, 1),
                new ElasticsearchIndexInfo("logs-sys-2026", "yellow", "open", 42, 4096, 1, 0),
            });
    }

    public Task<ElasticsearchIndexDetail> GetIndexDetailAsync(
        DatabaseConnection connection,
        string index,
        CancellationToken ct)
    {
        DetailIndex = index;
        if (Error is not null)
        {
            throw Error;
        }

        if (IndexDetail is not null)
        {
            return Task.FromResult(IndexDetail);
        }

        using var empty = JsonDocument.Parse("{}");
        return Task.FromResult(new ElasticsearchIndexDetail(
            index,
            empty.RootElement.Clone(),
            empty.RootElement.Clone(),
            124312,
            48211320));
    }

    public Task<ElasticsearchDocumentPage> DocumentsAsync(
        DatabaseConnection connection,
        string index,
        int from,
        int size,
        string? q,
        DateTimeOffset? start,
        DateTimeOffset? end,
        string? field,
        IReadOnlyList<ElasticsearchFilterRow>? filters,
        string? logic,
        CancellationToken ct)
    {
        DocumentsIndex = index;
        DocumentsFrom = from;
        DocumentsSize = size;
        DocumentsQ = q;
        DocumentsStart = start;
        DocumentsEnd = end;
        DocumentsField = field;
        DocumentsFilters = filters;
        DocumentsLogic = logic;
        // The real compiler runs here so a bad row fails with a genuine
        // ElasticsearchFilterException, exactly like the production service.
        if (filters is { Count: > 0 })
        {
            ElasticsearchFilterCompiler.Compile(filters, logic);
        }

        if (Error is not null)
        {
            throw Error;
        }

        return Task.FromResult(DocumentPage
            ?? new ElasticsearchDocumentPage(124312, from, size, 12, Array.Empty<ElasticsearchHit>()));
    }

    public Task<IReadOnlyList<ElasticsearchHistogramBucket>> HistogramAsync(
        DatabaseConnection connection,
        string index,
        string? q,
        DateTimeOffset? start,
        DateTimeOffset? end,
        string? field,
        int buckets,
        IReadOnlyList<ElasticsearchFilterRow>? filters,
        string? logic,
        CancellationToken ct)
    {
        HistogramIndex = index;
        HistogramQ = q;
        HistogramStart = start;
        HistogramEnd = end;
        HistogramField = field;
        HistogramBucketsRequested = buckets;
        if (filters is { Count: > 0 })
        {
            ElasticsearchFilterCompiler.Compile(filters, logic);
        }

        if (Error is not null)
        {
            throw Error;
        }

        return Task.FromResult(HistogramBuckets
            ?? (IReadOnlyList<ElasticsearchHistogramBucket>)new[]
            {
                new ElasticsearchHistogramBucket(1760000000000L, 1204) { Interval = "1d" },
                new ElasticsearchHistogramBucket(1760086400000L, 3855) { Interval = "1d" },
            });
    }

    public Task<ElasticsearchSearchResult> SearchAsync(
        DatabaseConnection connection,
        string index,
        JsonElement? query,
        JsonElement? sort,
        JsonElement? aggs,
        int size,
        int from,
        IReadOnlyList<string>? sourceIncludes,
        CancellationToken ct)
    {
        SearchIndex = index;
        SearchQuery = query;
        SearchSort = sort;
        SearchAggs = aggs;
        SearchSize = size;
        SearchFrom = from;
        SearchSourceIncludes = sourceIncludes;
        if (Error is not null)
        {
            throw Error;
        }

        return Task.FromResult(SearchResult
            ?? new ElasticsearchSearchResult(812, 9, Array.Empty<ElasticsearchHit>(), null));
    }

    public Task<long> CountAsync(
        DatabaseConnection connection,
        string index,
        JsonElement? query,
        CancellationToken ct)
    {
        CountIndex = index;
        CountQuery = query;
        if (Error is not null)
        {
            throw Error;
        }

        return Task.FromResult(CountResult);
    }
}
