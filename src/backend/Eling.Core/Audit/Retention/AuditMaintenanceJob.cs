using System.Text.Json;
using Eling.Core.Audit.Storage;

namespace Eling.Core.Audit.Retention;

public sealed class AuditMaintenanceJob(AuditFileStore store, AuditOptions options, TimeProvider timeProvider)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var rollupDir = Path.Combine(store.GetCurrentFilePath(AuditTrail.Change, now), "..", "..", "rollups");
        rollupDir = Path.GetFullPath(rollupDir);
        Directory.CreateDirectory(rollupDir);

        foreach (var trail in new[] { AuditTrail.Change, AuditTrail.Access })
        {
            var dir = Path.Combine(Path.GetDirectoryName(store.GetCurrentFilePath(trail, now))!);
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.GetFiles(dir, "*.jsonl"))
            {
                var date = DateTimeOffset.Parse(Path.GetFileNameWithoutExtension(file));
                if (!AuditRetentionPolicy.IsExpired(trail, date, now, options)) continue;

                var entries = (await File.ReadAllLinesAsync(file, cancellationToken))
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .Select(l => JsonSerializer.Deserialize(l, AuditJsonContext.Default.AuditEvent)!)
                    .ToList();

                foreach (var group in entries.GroupBy(e => new { e.Category, e.Action, e.Actor, e.Outcome }))
                {
                    var durations = group.Select(e => e.DurationMs).Where(d => d.HasValue).Select(d => d!.Value).OrderBy(d => d).ToList();
                    var rollup = new AuditRollup(
                        date, trail, group.Key.Category.ToString(), group.Key.Action, group.Key.Actor, group.Key.Outcome.ToString(),
                        group.Count(),
                        durations.Count > 0 ? durations[0] : null,
                        durations.Count > 0 ? durations[durations.Count / 2] : null,
                        durations.Count > 0 ? durations[^1] : null);
                    await File.AppendAllTextAsync(
                        Path.Combine(rollupDir, $"{date:yyyy-MM-dd}.jsonl"),
                        JsonSerializer.Serialize(rollup, AuditJsonContext.Default.AuditRollup) + "\n",
                        cancellationToken);
                }

                File.Delete(file);
            }
        }
    }
}
