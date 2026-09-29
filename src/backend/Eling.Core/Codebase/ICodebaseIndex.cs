namespace Eling.Core.Codebase;

public interface ICodebaseIndex
{
    Task EnsureCreatedAsync(CancellationToken ct = default);
    Task<CodebaseFileRecord?> GetFileAsync(string path, CancellationToken ct = default);
    Task ReplaceChunksAsync(string path, string hash, long mtime, long size, IReadOnlyList<CodebaseChunk> chunks, CancellationToken ct = default);
    Task DeleteFileAsync(string path, CancellationToken ct = default);
    Task<IReadOnlyList<CodebaseHit>> SearchPorterAsync(string ftsQuery, int limit, string? pathPrefix = null, string? filePattern = null, CancellationToken ct = default);
    Task<IReadOnlyList<CodebaseHit>> SearchTrigramAsync(string ftsQuery, int limit, string? pathPrefix = null, string? filePattern = null, CancellationToken ct = default);
    Task<CodebaseStats> GetStatsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListPathsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<CodebaseFileEntry>> ListRecentFilesAsync(int limit, CancellationToken ct = default);
    string DbPath { get; }
}
