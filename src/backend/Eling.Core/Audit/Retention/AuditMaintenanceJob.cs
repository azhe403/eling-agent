using System.Text.Json;
using Eling.Core.Audit.Storage;

namespace Eling.Core.Audit.Retention;

public sealed class AuditMaintenanceJob(AuditFileStore store, AuditOptions options, TimeProvider timeProvider)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var rollupDir = Path.GetFullPath(
            Path.Combine(store.GetCurrentFilePath(AuditTrail.Change, now), "..", "..", "rollups"));
        Directory.CreateDirectory(rollupDir);

        // Phase 1 — read. Gather every CLOSED day across both trails in either
        // the raw (.jsonl) or the production-compressed (.jsonl.gz) form, fully
        // read, before anything is written or deleted. A crash anywhere up to
        // the write phase leaves the store untouched, so the pass is safe to
        // retry.
        //
        // Folding is not gated on expiry (amendment C3): rollups are the
        // long-term aggregate retained indefinitely, so a day whose raw file is
        // lost before its window elapses must still have its aggregate — and the
        // rollup view must not be empty for the change trail during its long
        // retention window. Rolling up only expired days left the change trail
        // without any aggregate for its whole 365-day window.
        var closedDays = new List<AuditDayFiles.AuditDay>();
        foreach (var trail in new[] { AuditTrail.Change, AuditTrail.Access })
        {
            var dir = Path.GetDirectoryName(store.GetCurrentFilePath(trail, now))!;
            await foreach (var day in AuditDayFiles.ReadAsync(dir, trail, cancellationToken))
            {
                // Any day strictly before the current one is closed; the current
                // day is still being appended to.
                if (day.Date >= today) continue;
                closedDays.Add(day);
            }
        }

        // Phase 2 — plan. Accumulate each date's rollup lines in memory. A single
        // date can be spanned by both trails; their lines share one rollup file.
        var rollupLinesByDate = new Dictionary<DateTimeOffset, List<string>>();
        foreach (var day in closedDays)
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
        //
        // The day is compared as a day label, not as an instant (see SameDay): a
        // rollup line written by the transitional build on a non-UTC machine carries
        // local midnight of the day it labels ("2026-08-01T00:00:00+07:00"), while
        // this fold normalizes the same day to UTC ("2026-08-01T00:00:00+00:00") —
        // the same day, two instants seven hours apart. Both are also what names
        // the rollup file, so the label is the identity that has to match.
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
                    var reFolded = closedDays.Any(d => d.Trail == prior.Trail && SameDay(d.Date, prior.Date));
                    if (reFolded) continue;
                    kept.Add(line);
                }
            }

            if (kept.Count == 0 && newLines.Count == 0) continue;
            var content = string.Join("\n", kept.Concat(newLines)) + "\n";
            await File.WriteAllTextAsync(path, content, cancellationToken);
        }

        // Phase 4 — delete. Only after every rollup above is written, remove the raw
        // day files this pass folded AND whose retention window has elapsed
        // (§13). The gate is expiry, not folding: deleting every folded day
        // unconditionally stays safe exactly because every closed day is now
        // always folded, so no day can disappear without its aggregate — the
        // unexpired ones are simply left for the read API. A day that produced no
        // rollup lines (empty/whitespace content) is still deleted on purpose — a
        // "delete only if a rollup exists" guard would strand such data-free
        // files forever.
        foreach (var day in closedDays)
        {
            if (!AuditRetentionPolicy.IsExpired(day.Trail, day.Date, now, options)) continue;
            File.Delete(day.SourceFile);
        }
    }

    /// <summary>
    /// Whether two rollup labels denote the same day, whatever offset each writer
    /// stamped on its midnight. Every writer emits midnight of the day it labels
    /// in its own frame — the transitional build in local time, the UTC-normalized
    /// build in UTC — so the date the label reads is the one identity both share,
    /// and it is the same date that names the rollup file
    /// (<c>$"{date:yyyy-MM-dd}"</c>). Comparing the values as instants instead
    /// (DateTimeOffset equality, or UtcDateTime equality, which is the same
    /// comparison) would read "2026-08-01T00:00:00+07:00" and
    /// "2026-08-01T00:00:00+00:00" as different days and leave a permanent
    /// duplicate aggregate for one of them.
    /// </summary>
    private static bool SameDay(DateTimeOffset left, DateTimeOffset right) => left.Date == right.Date;

    private static IEnumerable<string> RollupLines(DateTimeOffset date, AuditTrail trail, IReadOnlyList<AuditEvent> entries)
    {
        foreach (var group in entries.GroupBy(e => new { e.Category, e.Action, e.Actor, e.Outcome }))
        {
            var durations = group.Select(e => e.DurationMs).Where(d => d.HasValue).Select(d => d!.Value).OrderBy(d => d).ToList();
            // Typed category/outcome, so the registered kebab-case converters
            // spell them exactly as the JSONL entries do (amendment C1).
            var rollup = new AuditRollup(
                date,
                trail,
                group.Key.Category,
                group.Key.Action,
                group.Key.Actor,
                group.Key.Outcome,
                group.Count(),
                durations.Count > 0 ? durations[0] : null,
                durations.Count > 0 ? durations[durations.Count / 2] : null,
                durations.Count > 0 ? durations[^1] : null);
            yield return JsonSerializer.Serialize(rollup, AuditJsonContext.Default.AuditRollup);
        }
    }
}
