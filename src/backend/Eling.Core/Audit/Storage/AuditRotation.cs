using System.IO.Compression;

namespace Eling.Core.Audit.Storage;

public static class AuditRotation
{
    /// <summary>
    /// Lazy, lock-internal rotation. Any day file other than <paramref name="now"/>'s
    /// date is compressed and removed; the current day file is left untouched.
    /// No background timer is used, so the behaviour is deterministic in tests.
    /// </summary>
    public static async Task<string> RotateIfNeededAsync(
        AuditFileStore store,
        AuditTrail trail,
        DateTimeOffset now,
        bool compress,
        CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(store.GetCurrentFilePath(trail, now))!;
        var current = Path.GetFileName(store.GetCurrentFilePath(trail, now));
        if (!Directory.Exists(dir)) return store.GetCurrentFilePath(trail, now);

        foreach (var file in Directory.GetFiles(dir, "*.jsonl"))
        {
            if (string.Equals(Path.GetFileName(file), current, StringComparison.Ordinal)) continue;

            if (compress)
            {
                var target = file + ".gz";
                await using (var input = File.OpenRead(file))
                await using (var output = File.Create(target))
                await using (var gz = new GZipStream(output, CompressionLevel.SmallestSize))
                {
                    await input.CopyToAsync(gz, cancellationToken);
                }
                File.Delete(file);
            }
        }

        return store.GetCurrentFilePath(trail, now);
    }
}
