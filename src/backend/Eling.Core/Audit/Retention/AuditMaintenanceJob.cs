using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Eling.Core.Audit.Storage;

namespace Eling.Core.Audit.Retention;

public sealed class AuditMaintenanceJob(AuditFileStore store, AuditOptions options, TimeProvider timeProvider)
{
    private const string RawExtension = ".jsonl";
    private const string GzipExtension = ".jsonl.gz";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var rollupDir = Path.GetFullPath(
            Path.Combine(store.GetCurrentFilePath(AuditTrail.Change, now), "..", "..", "rollups"));
        Directory.CreateDirectory(rollupDir);

        // Phase 1 — read. Gather every expired day across both trails in either the
        // raw (.jsonl) or the production-compressed (.jsonl.gz) form, fully read,
        // before anything is written or deleted. A crash anywhere up to the write
        // phase leaves the store untouched, so the pass is safe to retry.
        var expiredDays = new List<ExpiredDay>();
        foreach (var trail in new[] { AuditTrail.Change, AuditTrail.Access })
        {
            var dir = Path.GetDirectoryName(store.GetCurrentFilePath(trail, now))!;
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.EnumerateFiles(dir)
                .Where(f => f.EndsWith(RawExtension, StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(GzipExtension, StringComparison.OrdinalIgnoreCase)))
            {
                if (TryParseDateStem(file) is not DateTimeOffset date) continue;
                if (!AuditRetentionPolicy.IsExpired(trail, date, now, options)) continue;

                // Dedupe both forms for one (trail, date): only reachable when a
                // rotation crashed between writing the .gz and deleting the raw.
                // Prefer the compressed form and skip the raw so the day is not
                // counted twice; the orphaned raw is folded on the next pass.
                if (file.EndsWith(RawExtension, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(file + GzipExtension))
                {
                    continue;
                }

                var entries = await ReadEntriesAsync(file, cancellationToken);
                expiredDays.Add(new ExpiredDay(file, trail, date, entries));
            }
        }

        // Phase 2 — plan. Accumulate each date's rollup lines in memory. A single
        // date can be spanned by both trails; their lines share one rollup file.
        var rollupLinesByDate = new Dictionary<DateTimeOffset, List<string>>();
        foreach (var day in expiredDays)
        {
            if (!rollupLinesByDate.TryGetValue(day.Date, out var lines))
            {
                lines = new List<string>();
                rollupLinesByDate[day.Date] = lines;
            }
            lines.AddRange(RollupLines(day.Date, day.Trail, day.Entries));
        }

        // Phase 3 — write/merge. Rewrite each folded date's rollup once. The prior
        // file is read and every line whose (Trail, Date) this pass does NOT re-fold
        // is kept, so a change day folded months after the access day augments the
        // file instead of clobbering the access aggregate the design retains forever.
        // Lines whose (Trail, Date) this pass DOES re-fold are dropped here and
        // replaced by this pass's fresh lines — which keeps the pass idempotent per
        // (trail, date). Read-before-write keeps a crash to old-or-new, never a mix.
        foreach (var pair in rollupLinesByDate)
        {
            var date = pair.Key;
            var newLines = pair.Value;
            var path = Path.Combine(rollupDir, $"{date:yyyy-MM-dd}.jsonl");

            var kept = new List<string>();
            if (File.Exists(path))
            {
                foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var prior = JsonSerializer.Deserialize(line, AuditJsonContext.Default.AuditRollup);
                    if (prior is null) continue;
                    var reFolded = expiredDays.Any(d => d.Trail == prior.Trail && d.Date == prior.Date);
                    if (reFolded) continue;
                    kept.Add(line);
                }
            }

            if (kept.Count == 0 && newLines.Count == 0) continue;
            var content = string.Join("\n", kept.Concat(newLines)) + "\n";
            await File.WriteAllTextAsync(path, content, cancellationToken);
        }

        // Phase 4 — delete. Only after every rollup above is written, remove the raw
        // day files this pass folded: rollup-before-deletion is the ordering that
        // makes a crash leave the old or the new state, never a partial one. A day
        // that produced no rollup lines (empty/whitespace content) is still deleted
        // on purpose — a "delete only if a rollup exists" guard would strand such
        // data-free files forever.
        foreach (var day in expiredDays)
        {
            File.Delete(day.RawPath);
        }
    }

    /// <summary>
    /// The date encoded in a day file's name, stripping the full compound suffix:
    /// <c>2026-09-10.jsonl.gz</c> and <c>2026-09-10.jsonl</c> both yield the stem
    /// <c>2026-09-10</c>. The stem must match the writer-generated
    /// <c>yyyy-MM-dd</c> shape exactly; a non-matching name returns <c>null</c> so
    /// one stray file cannot abort the whole pass.
    /// </summary>
    private static DateTimeOffset? TryParseDateStem(string file)
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

    private static IEnumerable<string> RollupLines(DateTimeOffset date, AuditTrail trail, IReadOnlyList<AuditEvent> entries)
    {
        foreach (var group in entries.GroupBy(e => new { e.Category, e.Action, e.Actor, e.Outcome }))
        {
            var durations = group.Select(e => e.DurationMs).Where(d => d.HasValue).Select(d => d!.Value).OrderBy(d => d).ToList();
            var rollup = new AuditRollup(
                date, trail, group.Key.Category.ToString(), group.Key.Action, group.Key.Actor, group.Key.Outcome.ToString(),
                group.Count(),
                durations.Count > 0 ? durations[0] : null,
                durations.Count > 0 ? durations[durations.Count / 2] : null,
                durations.Count > 0 ? durations[^1] : null);
            yield return JsonSerializer.Serialize(rollup, AuditJsonContext.Default.AuditRollup);
        }
    }

    /// <summary>
    /// One expired day's raw file, its trail/date, and the entries read from it,
    /// carried from the read phase to the write/delete phases so the whole pass is
    /// read-then-write-then-delete (never append). A record, not a tuple, per the
    /// repo's no-tuple rule.
    /// </summary>
    private sealed record ExpiredDay(
        string RawPath,
        AuditTrail Trail,
        DateTimeOffset Date,
        IReadOnlyList<AuditEvent> Entries);
}
