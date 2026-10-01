using Eling.Core.Codebase;

namespace Eling.Backend.Tests;

/// <summary>
/// Test double for <see cref="ICodebaseIndex"/> that records which relaxation
/// pass was issued, in order. Pinning the ladder through a double keeps the
/// test off SQLite's tokenizers, whose scoring would otherwise decide the
/// result and make a wrong pass order look correct.
/// </summary>
internal sealed class RecordingCodebaseIndex : ICodebaseIndex
{
    private readonly Queue<IReadOnlyList<CodebaseHit>> _porter = new();
    private readonly Queue<IReadOnlyList<CodebaseHit>> _trigram = new();
    private int _sequence;

    /// <summary>
    /// One entry per query issued, in order: "porter:AND", "porter:OR",
    /// "trigram:AND", "trigram:OR".
    /// </summary>
    public List<string> Calls { get; } = [];

    public string DbPath => "recording";

    /// <summary>Queues the result of the next porter pass.</summary>
    public void QueuePorter(int hitCount) => _porter.Enqueue(MakeHits(hitCount));

    /// <summary>Queues the result of the next trigram pass.</summary>
    public void QueueTrigram(int hitCount) => _trigram.Enqueue(MakeHits(hitCount));

    public Task<IReadOnlyList<CodebaseHit>> SearchPorterAsync(
        string ftsQuery,
        int limit,
        string? pathPrefix = null,
        string? filePattern = null,
        CancellationToken ct = default)
    {
        Calls.Add($"porter:{Operator(ftsQuery)}");
        return Task.FromResult(Take(_porter));
    }

    public Task<IReadOnlyList<CodebaseHit>> SearchTrigramAsync(
        string ftsQuery,
        int limit,
        string? pathPrefix = null,
        string? filePattern = null,
        CancellationToken ct = default)
    {
        Calls.Add($"trigram:{Operator(ftsQuery)}");
        return Task.FromResult(Take(_trigram));
    }

    private static IReadOnlyList<CodebaseHit> Take(Queue<IReadOnlyList<CodebaseHit>> queue)
        => queue.Count > 0 ? queue.Dequeue() : Array.Empty<CodebaseHit>();

    private static string Operator(string ftsQuery)
        => ftsQuery.Contains(" AND ", StringComparison.Ordinal) ? "AND" : "OR";

    // Hit keys must be unique per pass, otherwise Merge would collapse
    // consecutive passes into one and the counts would read as duplicates.
    private IReadOnlyList<CodebaseHit> MakeHits(int hitCount)
    {
        var pass = _sequence++;
        return Enumerable.Range(0, hitCount)
            .Select(i => new CodebaseHit($"hit-{pass}-{i}.cs", 1, 2, "body", hitCount - i))
            .ToList();
    }

    public Task EnsureCreatedAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<CodebaseFileRecord?> GetFileAsync(string path, CancellationToken ct = default)
        => Task.FromResult<CodebaseFileRecord?>(null);

    public Task ReplaceChunksAsync(string path, string hash, long mtime, long size, IReadOnlyList<CodebaseChunk> chunks, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task DeleteFileAsync(string path, CancellationToken ct = default) => Task.CompletedTask;

    public Task<CodebaseStats> GetStatsAsync(CancellationToken ct = default)
        => Task.FromResult(new CodebaseStats(0, 0, null, DbPath));

    public Task<IReadOnlyList<string>> ListPathsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>([]);

    public Task<IReadOnlyList<CodebaseFileEntry>> ListRecentFilesAsync(int limit, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<CodebaseFileEntry>>([]);

    public Task<IReadOnlyList<CodebaseChunk>> ListChunksAsync(string path, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<CodebaseChunk>>([]);
}
