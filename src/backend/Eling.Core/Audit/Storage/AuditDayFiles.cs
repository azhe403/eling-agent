using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Eling.Core.Audit.Storage;

/// <summary>
/// One day-file reader for the whole feature. A trail directory holds one file
/// per day: <c>yyyy-MM-dd.jsonl</c> while the day is open and
/// <c>yyyy-MM-dd.jsonl.gz</c> once rotation has compressed the closed day. The
/// maintenance job and the index rebuild both read days through here, so they
/// cannot disagree about which days exist or how a compressed one is read —
/// the rebuild used to glob <c>*.jsonl</c>, which silently skipped every
/// compressed day an index whose contract is "rescans the JSONL" should have
/// seen.
/// </summary>
internal static class AuditDayFiles
{
    private const string RawExtension = ".jsonl";
    private const string GzipExtension = ".jsonl.gz";
    private const string GzipSuffix = ".gz";

    /// <summary>
    /// One day of one trail: the date in its file name, the trail it belongs
    /// to, every entry read from it, and the file it came from. A record, not a
    /// tuple, per the repo's no-tuple rule; the source file is carried because
    /// the maintenance job deletes exactly the file it folded and the index
    /// records which file each row came from.
    /// </summary>
    internal sealed record AuditDay(
        DateTimeOffset Date,
        AuditTrail Trail,
        IReadOnlyList<AuditEvent> Entries,
        string SourceFile);

    /// <summary>
    /// Every day file in <paramref name="directory"/>, one at a time, oldest
    /// first. A day can exist in both forms only when a rotation crashed
    /// between writing the .gz and deleting the raw; the compressed form wins
    /// so the day is not folded twice.
    /// </summary>
    internal static async IAsyncEnumerable<AuditDay> ReadAsync(
        string directory,
        AuditTrail trail,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directory)) yield break;

        foreach (var file in Directory.EnumerateFiles(directory)
            .Where(IsDayFile)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            if (TryParseDate(file) is not DateTimeOffset date) continue;
            if (file.EndsWith(RawExtension, StringComparison.OrdinalIgnoreCase)
                && File.Exists(file + GzipSuffix))
            {
                continue;
            }

            yield return new AuditDay(
                date,
                trail,
                await ReadEntriesAsync(file, cancellationToken),
                file);
        }
    }

    /// <summary>
    /// Whether the file name is a day file at all: the stem must match the
    /// writer-generated <c>yyyy-MM-dd</c> shape exactly, so one stray file
    /// cannot abort the whole pass.
    /// </summary>
    private static bool IsDayFile(string file) => TryParseDate(file).HasValue;

    /// <summary>
    /// The date encoded in a day file's name, stripping the full compound
    /// suffix: <c>2026-09-10.jsonl.gz</c> and <c>2026-09-10.jsonl</c> both yield
    /// the stem <c>2026-09-10</c>. Returns <c>null</c> for anything else.
    /// </summary>
    private static DateTimeOffset? TryParseDate(string file)
    {
        var name = Path.GetFileName(file);
        var stem = name.EndsWith(GzipExtension, StringComparison.OrdinalIgnoreCase)
            ? name[..^GzipExtension.Length]
            : Path.GetFileNameWithoutExtension(name);
        return DateTimeOffset.TryParseExact(
            stem, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    private static async Task<List<AuditEvent>> ReadEntriesAsync(string file, CancellationToken cancellationToken)
    {
        IEnumerable<string> lines;
        if (file.EndsWith(GzipExtension, StringComparison.OrdinalIgnoreCase))
        {
            await using var stream = File.OpenRead(file);
            await using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip);
            lines = (await reader.ReadToEndAsync(cancellationToken)).Split('\n');
        }
        else
        {
            lines = await File.ReadAllLinesAsync(file, cancellationToken);
        }

        return lines
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonSerializer.Deserialize(l, AuditJsonContext.Default.AuditEvent)!)
            .ToList();
    }
}
