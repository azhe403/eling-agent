using System.Text.Json;

namespace Eling.Core.Audit.Storage;

/// <summary>
/// Appends audit entries to per-trail, per-day JSONL files. Two layers keep every
/// line whole: a per-process writer gate serializes in-process appenders, and the
/// file is opened for append with a write-denying share mode whose sharing
/// violation is retried with bounded backoff for genuine cross-process
/// contention. The OS releases the handle when a process dies, so no stale lock
/// can survive.
/// </summary>
public sealed class AuditFileStore(string auditRoot, TimeProvider timeProvider)
{
    private const int MaxAttempts = 5;

    /// <summary>
    /// Serializes in-process appenders (Design §7.1). <c>FileShare.Read</c> denies a
    /// second write handle to the same file, so two stores appending at once would
    /// otherwise spend their retries on each other; the bounded retry below is what
    /// answers a *cross-process* share violation.
    /// </summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public string GetCurrentFilePath(AuditTrail trail, DateTimeOffset now)
    {
        var dir = Path.Combine(auditRoot, trail == AuditTrail.Change ? "changes" : "access");
        return Path.Combine(dir, $"{now:yyyy-MM-dd}.jsonl");
    }

    public async Task AppendAsync(
        AuditTrail trail,
        IReadOnlyList<AuditEvent> entries,
        bool flushToDisk,
        CancellationToken cancellationToken)
    {
        if (entries.Count == 0) return;

        var now = timeProvider.GetUtcNow();
        var path = GetCurrentFilePath(trail, now);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var payload = string.Concat(entries.Select(e =>
            JsonSerializer.Serialize(e, AuditJsonContext.Default.AuditEvent) + "\n"));

        await Gate.WaitAsync(cancellationToken);
        try
        {
            await AppendLineAsync(path, payload, flushToDisk, cancellationToken);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task AppendLineAsync(
        string path, string payload, bool flushToDisk, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var stream = new FileStream(
                    path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 4096,
                    useAsync: true);

                var bytes = System.Text.Encoding.UTF8.GetBytes(payload);
                await stream.WriteAsync(bytes, cancellationToken);
                if (flushToDisk)
                {
                    await stream.FlushAsync(cancellationToken);
                    stream.Flush(flushToDisk: true);
                }
                return;
            }
            catch (IOException) when (attempt < MaxAttempts)
            {
                await Task.Delay(20 * attempt, cancellationToken);
            }
        }
    }
}
