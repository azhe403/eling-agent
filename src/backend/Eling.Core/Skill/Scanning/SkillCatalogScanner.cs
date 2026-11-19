using System.Collections;
using System.Globalization;
using Eling.Core.Scope;
using Eling.Core.Skill.Serialization;
using Eling.Core.Skill.Storage;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;

namespace Eling.Core.Skill.Scanning;

/// <summary>
/// File-backed catalog scanner rooted at a catalog root (default:
/// <c>~/.agents</c>, via <see cref="SkillCatalogPaths.CatalogDirectory"/>).
/// </summary>
/// <remarks>
/// Ownership rules (spec §3.2/§10): external skills are indexed through
/// <see cref="ISkillIndex.AdoptExternalScanResultAsync"/> with an index-assigned
/// ULID that is NEVER written back to the file; external files are never
/// rewritten, renamed or deleted by the scanner. Eling-origin rows are matched
/// by their frontmatter ULID, which is what makes folder renames resumable
/// after a crash (the row is re-keyed to the new name instead of duplicated).
/// Missing detection consults only the ACTIVE index set — trashed rows are
/// expected to have no active folder and are left alone. An absent
/// <c>skills/</c> directory disables missing detection entirely (the catalog
/// is not mounted, not empty); every otherwise-valid row is kept.
/// </remarks>
public sealed class SkillCatalogScanner : ISkillCatalogScanner
{
    private const int MaxDescriptionLength = Skill.MaxDescriptionLength;

    private readonly ISkillIndex _index;
    private readonly string _skillsDir;
    private readonly ILogger<SkillCatalogScanner>? _logger;

    public SkillCatalogScanner(ISkillIndex index, string? catalogRoot = null, ILogger<SkillCatalogScanner>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        _index = index;
        var root = Path.GetFullPath(catalogRoot ?? SkillCatalogPaths.CatalogDirectory);
        CatalogRoot = root;
        _skillsDir = Path.Combine(root, SkillCatalogPaths.SkillsSubdir);
        _logger = logger;
    }

    /// <summary>The catalog root this instance scans (absolute path).</summary>
    public string CatalogRoot { get; }

    public async Task<ScanReport> ScanAsync()
    {
        // Pre-scan snapshot of the active index. Drives rename detection (by
        // id), external id reuse (by name) and the Eling-name occupation check.
        // Trashed rows are deliberately excluded from the active set.
        var active = await _index.ListAsync(includeTrashed: false, origin: null);
        var byId = new Dictionary<string, Skill>(active.Count, StringComparer.Ordinal);
        var byName = new Dictionary<string, Skill>(active.Count, StringComparer.Ordinal);
        foreach (var row in active)
        {
            byId[row.Id.Value] = row;
            byName[row.Name] = row;
        }

        // Occupation check for foreign folders is seeded from the FULL index
        // (includeTrashed): any Eling row holding a name — active or trashed —
        // means the folder is ours or was ours, so a same-named foreign folder
        // is a failed adoption, never an external re-key that would delete the
        // trashed Eling row.
        var elingOccupied = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in await _index.ListAsync(includeTrashed: true, origin: null))
        {
            if (row.Origin == SkillOrigin.Eling)
            {
                elingOccupied.Add(row.Name);
            }
        }

        var discovered = 0;
        var external = 0;
        var failed = 0;
        var renamed = 0;
        var renamedFrom = new HashSet<string>(StringComparer.Ordinal);
        var failures = new List<SkillScanFailure>();

        if (Directory.Exists(_skillsDir))
        {
            // Deterministic order: alphabetical scan, so name claims are stable
            // across runs. Eling rows & external rows keep their precedence
            // rules; a folder that fails kebab-case can be indexed only when
            // its frontmatter carries a valid kebab-case `name` (the display
            // name) and that name is free.
            foreach (var folder in Directory.EnumerateDirectories(_skillsDir).OrderBy(d => d, StringComparer.Ordinal))
            {
                var folderName = Path.GetFileName(folder);

                if (folderName.StartsWith('.'))
                {
                    // Our own temp/backup artifacts from an interrupted write.
                    continue;
                }

                // The display name is the name the index and the UI use. For
                // kebab-valid folders it is the folder name (unchanged); for
                // non-kebab folders (sync flatten artifacts) it comes from the
                // frontmatter `name`, and the index row's body_path must keep
                // the real filesystem location under this folder.
                var displayName = folderName;
                string? externalBodyPath = null;
                if (!Skill.ValidateName(folderName))
                {
                    var relaxed = await TryRelaxFolderNameAsync(folder, folderName, elingOccupied, failures);
                    if (relaxed is null)
                    {
                        failed++;
                        continue;
                    }

                    displayName = relaxed.Name;
                    externalBodyPath = relaxed.BodyPath;
                    // Logged per folder, and the scan runs on every MCP host
                    // spawn — one line per non-kebab folder used to fill the
                    // 4 KB stderr pipe on Windows and stall startup before the
                    // stdio handshake could answer. Debug keeps it available
                    // without taxing agent startup.
                    _logger?.LogDebug(
                        "Indexing non-kebab-case folder {Folder} under frontmatter name '{Name}'.",
                        folder,
                        displayName);
                }

                var file = Path.Combine(folder, FileSystemSkillStorage.SkillFileName);
                if (!File.Exists(file))
                {
                    _logger?.LogWarning("Skipping skill folder {Folder}: SKILL.md is missing.", folder);
                    failed++;
                    failures.Add(new SkillScanFailure(folder, displayName, "SKILL.md is missing"));
                    continue;
                }

                string raw;
                try
                {
                    raw = await File.ReadAllTextAsync(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    _logger?.LogWarning(ex, "Skipping skill folder {Folder}: SKILL.md is unreadable.", folder);
                    failed++;
                    failures.Add(new SkillScanFailure(folder, displayName, $"SKILL.md is unreadable: {ex.Message}"));
                    continue;
                }

                Skill? eling;
                try
                {
                    eling = SkillFileFormat.ParseSkill(raw, displayName);
                }
                catch (SkillFrontMatterException ex)
                {
                    var outcome = IndexNonEling(displayName, raw, byName, elingOccupied, ex);
                    switch (outcome)
                    {
                        case ExternalAdoption adoption:
                            try
                            {
                                await _index.AdoptExternalScanResultAsync(adoption.Skill, externalBodyPath ?? $"skills/{folderName}/SKILL.md");
                                if (adoption.IsNew)
                                {
                                    external++;
                                }
                            }
                            catch (ArgumentException adoptError)
                            {
                                _logger?.LogWarning(adoptError, "Skipping skill folder {Folder}: {Message}", folder, adoptError.Message);
                                failed++;
                                failures.Add(new SkillScanFailure(folder, displayName, adoptError.Message));
                            }

                            break;
                        case FailedAdoption failure:
                            failed++;
                            failures.Add(new SkillScanFailure(folder, displayName, failure.Reason));
                            break;
                    }

                    continue;
                }

                try
                {
                    if (byId.TryGetValue(eling.Id.Value, out var prior)
                        && !string.Equals(prior.Name, eling.Name, StringComparison.Ordinal))
                    {
                        renamed++;
                        renamedFrom.Add(prior.Name);
                    }
                    else if (!byId.ContainsKey(eling.Id.Value))
                    {
                        discovered++;
                    }

                    await _index.IndexAsync(eling);
                }
                catch (ArgumentException ex)
                {
                    // Vocabulary/scope violations reach here only if the parser
                    // missed them; treat as unindexable, never abort the scan.
                    _logger?.LogWarning(ex, "Skipping skill folder {Folder}: {Message}", folder, ex.Message);
                    failed++;
                    failures.Add(new SkillScanFailure(folder, displayName, ex.Message));
                }
            }
        }
        else
        {
            _logger?.LogWarning("Catalog skills directory {SkillsDirectory} does not exist; skipping missing-row detection.", _skillsDir);
        }

        var missing = 0;
        if (Directory.Exists(_skillsDir))
        {
            // A row is missing only when the filesystem path stored for it
            // (body_path) is gone. [..]
            var paths = await _index.ListPathsAsync();
            var catalogRootAbs = Path.GetFullPath(CatalogRoot);
            foreach (var row in active)
            {
                if (renamedFrom.Contains(row.Name))
                {
                    // The row was re-keyed to its new name during this scan;
                    // it is not missing.
                    continue;
                }

                if (paths.TryGetValue(row.Name, out var bodyPath))
                {
                    var absolute = Path.GetFullPath(
                        Path.Combine(catalogRootAbs, bodyPath.Replace('/', Path.DirectorySeparatorChar)));
                    if (File.Exists(absolute))
                    {
                        continue;
                    }
                }

                _logger?.LogWarning(
                    "Skill '{Name}' folder vanished; removing its row from the active index.", row.Name);
                await _index.RemoveAsync(row.Name);
                missing++;
            }
        }

        return new ScanReport(discovered, external, missing, failed, renamed, failures);
    }

    /// <summary>Display name and index-relative body path of a relaxed folder.</summary>
    private sealed record RelaxedFolder(
        string Name,
        string BodyPath);

    /// <summary>
    /// Non-kebab-case folders (e.g. a sync tool flattened a source path into
    /// one directory with double underscores) cannot be indexed by folder name.
    /// They can still be indexed as EXTERNAL when their SKILL.md frontmatter
    /// carries a valid kebab-case <c>name</c> — the relaxed entry displays that
    /// name, never the raw folder name, and the index row keeps the real
    /// filesystem location in <c>body_path</c>. Refusal (parse failure,
    /// missing/invalid frontmatter <c>name</c>, an Eling-occupied display name,
    /// or an id-bearing file that looks like an Eling file gone stale) records a
    /// failure with the reason.
    /// </summary>
    private async Task<RelaxedFolder?> TryRelaxFolderNameAsync(
        string folder,
        string folderName,
        IReadOnlySet<string> elingOccupied,
        List<SkillScanFailure> failures)
    {
        var file = Path.Combine(folder, FileSystemSkillStorage.SkillFileName);
        if (!File.Exists(file))
        {
            _logger?.LogWarning("Skipping skill folder {Folder}: SKILL.md is missing.", folder);
            failures.Add(new SkillScanFailure(
                folder,
                folderName,
                $"kebab-case violation: '{folderName}' is not a valid kebab-case skill name, and its SKILL.md is missing"));
            return null;
        }

        string raw;
        try
        {
            raw = await File.ReadAllTextAsync(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            failures.Add(new SkillScanFailure(folder, folderName, $"SKILL.md is unreadable: {ex.Message}"));
            return null;
        }

        var probe = FrontMatterProbe.Read(raw);

        if (probe.YamlInvalid)
        {
            failures.Add(new SkillScanFailure(folder, folderName, "frontmatter YAML invalid"));
            return null;
        }
        if (probe.HasUlidId)
        {
            failures.Add(new SkillScanFailure(folder, folderName, "name occupied by an Eling-managed skill (id present in frontmatter)"));
            return null;
        }

        var name = probe.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name) || !Skill.ValidateName(name))
        {
            failures.Add(new SkillScanFailure(
                folder,
                folderName,
                $"kebab-case violation: '{folderName}' is not a valid kebab-case skill name, and its frontmatter 'name' is missing or invalid"));
            return null;
        }

        if (elingOccupied.Contains(name))
        {
            failures.Add(new SkillScanFailure(folder, folderName, $"name occupied by an Eling-managed skill ('{name}')"));
            return null;
        }

        return new RelaxedFolder(name, $"skills/{folderName}/SKILL.md");
    }

    private AdoptionOutcome IndexNonEling(
        string name,
        string raw,
        IReadOnlyDictionary<string, Skill> byName,
        IReadOnlySet<string> elingOccupied,
        SkillFrontMatterException parseError)
    {
        var probe = FrontMatterProbe.Read(raw);

        // A ULID 'id' means somebody meant this to be one of ours: a broken
        // Eling file must never be reclassified as read-only external content.
        var claimsElingIdentity = probe.HasUlidId;
        // ANY Eling row under this name — active or trashed — means the folder
        // is ours (or was); externalizing would make it permanently read-only
        // and the external upsert would wipe the (possibly trashed) Eling row.
        var occupiesElingName = elingOccupied.Contains(name);

        if (probe.YamlInvalid || claimsElingIdentity || occupiesElingName)
        {
            var reason = probe.YamlInvalid
                ? "frontmatter YAML invalid"
                : occupiesElingName
                    ? "name occupied by an Eling-managed skill"
                    : parseError.Message;

            _logger?.LogWarning(
                parseError,
                "Skipping skill folder {Folder}: SKILL.md is not valid Eling frontmatter and cannot be treated as external content.",
                Path.Combine(_skillsDir, name));
            return new FailedAdoption(reason);
        }

        // Externally-supplied skill: index it, but never write back to the file.
        byName.TryGetValue(name, out var prior);
        var reuse = prior is { Origin: SkillOrigin.External } ? prior : null;

        var body = probe.Body;
        if (string.IsNullOrWhiteSpace(body))
        {
            body = raw;
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            _logger?.LogWarning(
                "Skipping skill folder {Folder}: SKILL.md has no body to index.",
                Path.Combine(_skillsDir, name));
            return new FailedAdoption("SKILL.md has no body to index");
        }

        var description = probe.Description;
        if (string.IsNullOrWhiteSpace(description))
        {
            description = FirstNonEmptyLine(body);
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            _logger?.LogWarning(
                "Skipping skill folder {Folder}: no description available in frontmatter or body.",
                Path.Combine(_skillsDir, name));
            return new FailedAdoption("no description available in frontmatter or body");
        }

        if (description.Length > MaxDescriptionLength)
        {
            description = description[..MaxDescriptionLength];
        }

        var now = DateTimeOffset.UtcNow;
        var createdAt = reuse?.CreatedAt ?? probe.CreatedAt ?? now;
        var updatedAt = reuse?.UpdatedAt ?? probe.UpdatedAt ?? createdAt;

        Skill skill;
        try
        {
            skill = new Skill(
                name,
                description,
                body,
                probe.Tags,
                relatedMemories: null,
                relatedSnippets: null,
                source: null,
                id: reuse?.Id ?? SkillId.NewId(),
                origin: SkillOrigin.External,
                createdAt: createdAt,
                updatedAt: updatedAt);
        }
        catch (ArgumentException ex)
        {
            _logger?.LogWarning(ex, "Skipping skill folder {Folder}: {Message}", Path.Combine(_skillsDir, name), ex.Message);
            return new FailedAdoption(ex.Message);
        }

        return new ExternalAdoption(skill, IsNew: reuse is null);
    }

    private static string? FirstNonEmptyLine(string text)
    {
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var stripped = trimmed.TrimStart('#').Trim();
            return stripped.Length > 0 ? stripped : trimmed;
        }

        return null;
    }

    private abstract record AdoptionOutcome;

    private sealed record ExternalAdoption(
        Skill Skill,
        bool IsNew) : AdoptionOutcome;

    private sealed record FailedAdoption(string Reason) : AdoptionOutcome;

    /// <summary>
    /// Best-effort structural read of a SKILL.md that is not valid Eling
    /// frontmatter. Never throws; on any YAML problem <see cref="YamlInvalid"/>
    /// is set so the caller can refuse to externalize.
    /// </summary>
    private sealed record FrontMatterProbe(
        bool YamlInvalid,
        bool HasUlidId,
        string? Name,
        string? Description,
        IReadOnlyCollection<string>? Tags,
        DateTimeOffset? CreatedAt,
        DateTimeOffset? UpdatedAt,
        string Body)
    {
        private const string Delimiter = "---";

        public static FrontMatterProbe Read(string raw)
        {
            var normalized = raw.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            if (!normalized.StartsWith(Delimiter + "\n", StringComparison.Ordinal))
            {
                // No frontmatter at all: treat the whole file as the body.
                return new FrontMatterProbe(false, false, null, null, null, null, null, normalized);
            }

            var yamlStart = Delimiter.Length + 1;
            var closing = normalized.IndexOf("\n" + Delimiter + "\n", yamlStart, StringComparison.Ordinal);
            string yaml;
            string body;
            if (closing >= 0)
            {
                yaml = normalized.Substring(yamlStart, closing - yamlStart);
                body = normalized.Substring(closing + 1 + Delimiter.Length + 1);
            }
            else if (normalized.EndsWith("\n" + Delimiter, StringComparison.Ordinal))
            {
                yaml = normalized.Substring(yamlStart, normalized.Length - ("\n" + Delimiter).Length - yamlStart);
                body = string.Empty;
            }
            else
            {
                // Opened a frontmatter block but never closed it: unreadable,
                // never externalizable.
                return new FrontMatterProbe(true, false, null, null, null, null, null, normalized);
            }

            Dictionary<string, object?>? map;
            try
            {
                map = new DeserializerBuilder()
                    .IgnoreUnmatchedProperties()
                    .Build()
                    .Deserialize<Dictionary<string, object?>>(yaml);
            }
            catch (Exception)
            {
                return new FrontMatterProbe(true, false, null, null, null, null, null, body);
            }

            if (map is null)
            {
                return new FrontMatterProbe(false, false, null, null, null, null, null, body);
            }

            return new FrontMatterProbe(
                YamlInvalid: false,
                HasUlidId: SkillId.TryParse(Scalar(map, "id"), out _),
                Name: Scalar(map, "name"),
                Description: Scalar(map, "description"),
                Tags: ReadTags(map),
                CreatedAt: ReadTimestamp(map, "createdAt"),
                UpdatedAt: ReadTimestamp(map, "updatedAt"),
                Body: body);
        }

        private static string? Scalar(IReadOnlyDictionary<string, object?> map, string key)
        {
            if (!map.TryGetValue(key, out var value) || value is null)
            {
                return null;
            }

            // A string is an IEnumerable<char> — it is a scalar, not a
            // collection; only sequences/mappings are rejected.
            return value is string s ? s : value is IDictionary or IEnumerable ? null : value.ToString();
        }

        private static IReadOnlyCollection<string>? ReadTags(IReadOnlyDictionary<string, object?> map)
        {
            try
            {
                if (!map.TryGetValue("tags", out var value) || value is not IEnumerable items || value is string)
                {
                    return null;
                }

                var tags = new List<string>();
                foreach (var item in items)
                {
                    if (item is not null)
                    {
                        tags.Add(item.ToString()!);
                    }
                }

                return tags;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static DateTimeOffset? ReadTimestamp(IReadOnlyDictionary<string, object?> map, string key)
        {
            if (!map.TryGetValue(key, out var value) || value is null)
            {
                return null;
            }

            switch (value)
            {
                case DateTimeOffset dto:
                    return dto;
                case DateTime dt:
                    return new DateTimeOffset(dt);
                case string s
                    when DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var parsed):
                    return parsed;
                default:
                    return null;
            }
        }
    }
}
