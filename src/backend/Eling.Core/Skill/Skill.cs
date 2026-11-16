using System.Text.RegularExpressions;

namespace Eling.Core.Skill;

/// <summary>
/// Optional upstream provenance of a skill ("where this came from"). Metadata
/// only — Eling never acts on it. Not serialized to frontmatter beyond the
/// canonical <c>source: { upstream, tracked }</c> shape.
/// </summary>
public sealed record SkillSource(string? Upstream, bool Tracked = false);

/// <summary>
/// The skill aggregate. Machine-global only — there are no scope tiers for
/// skills. <see cref="Id"/> is a ULID assigned at creation; <see cref="Name"/>
/// is the human-facing kebab-case identity and doubles as the folder name
/// under the catalog root. <see cref="Origin"/> is an ownership marker stored
/// in the index only; it is never written to frontmatter.
/// </summary>
public class Skill
{
    public const int MaxNameLength = 128;
    public const int MaxDescriptionLength = 512;

    private static readonly Regex NameRegex = new(@"^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);

    // Hyphen is intentionally NOT a separator: tag names commonly use
    // hyphens (e.g. "my-tag") and users expect them to survive as one tag.
    private static readonly Regex TagSeparatorRegex = new(@"[\s_./\\|]+", RegexOptions.Compiled);

    public SkillId Id { get; }
    public string Name { get; }
    public string Description { get; set; }
    public string Body { get; set; }
    public IReadOnlyCollection<string> Tags { get; set; }
    public IReadOnlyCollection<RelatedReference> RelatedMemories { get; set; }
    public IReadOnlyCollection<RelatedReference> RelatedSnippets { get; set; }
    public SkillSource? Source { get; set; }
    public SkillOrigin Origin { get; set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Skill(
        string name,
        string description,
        string body,
        IEnumerable<string>? tags = null,
        IEnumerable<RelatedReference>? relatedMemories = null,
        IEnumerable<RelatedReference>? relatedSnippets = null,
        SkillSource? source = null,
        SkillId? id = null,
        SkillOrigin origin = SkillOrigin.Eling,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? updatedAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!ValidateName(name))
        {
            throw new ArgumentException(
                $"Skill name must be lowercase kebab-case (^[a-z0-9]+(-[a-z0-9]+)*$) and at most {MaxNameLength} characters.",
                nameof(name));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (description.Length > MaxDescriptionLength)
        {
            throw new ArgumentException(
                $"Skill description must be at most {MaxDescriptionLength} characters.",
                nameof(description));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        Id = id ?? SkillId.NewId();
        Name = name;
        Description = description;
        // Body is stored verbatim; only emptiness is validated.
        Body = body;
        Tags = NormalizeTags(tags);
        RelatedMemories = relatedMemories?.ToList().AsReadOnly() ?? (IReadOnlyCollection<RelatedReference>)Array.Empty<RelatedReference>();
        RelatedSnippets = relatedSnippets?.ToList().AsReadOnly() ?? (IReadOnlyCollection<RelatedReference>)Array.Empty<RelatedReference>();
        Source = source;
        Origin = origin;
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow;
        UpdatedAt = updatedAt ?? CreatedAt;
    }

    /// <summary>
    /// True when <paramref name="name"/> is lowercase kebab-case
    /// (<c>^[a-z0-9]+(-[a-z0-9]+)*$</c>) and within <see cref="MaxNameLength"/>.
    /// The same rule decides whether a folder name is a valid skill name, so
    /// the folder name and the skill identity can never drift apart.
    /// </summary>
    public static bool ValidateName(string? name) =>
        !string.IsNullOrEmpty(name)
        && name.Length <= MaxNameLength
        && NameRegex.IsMatch(name);

    /// <summary>
    /// Normalize a tag list for indexability: split on common separators
    /// (whitespace, underscore, dot, slash, backslash, pipe) — hyphen is kept
    /// so names like "my-tag" survive as one tag — lower-case, trim, drop
    /// tokens shorter than 2 characters and remove duplicates. Used by the
    /// constructor, the indexer, and the search-side re-normalizer.
    /// </summary>
    public static IReadOnlyCollection<string> NormalizeTags(IEnumerable<string>? tags)
    {
        if (tags is null)
        {
            return Array.Empty<string>();
        }

        var tokens = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in tags)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var parts = TagSeparatorRegex.Split(raw.Trim().ToLowerInvariant());
            foreach (var part in parts)
            {
                var token = part.Trim();
                if (token.Length < 2)
                {
                    continue;
                }
                if (seen.Add(token))
                {
                    tokens.Add(token);
                }
            }
        }

        return tokens.AsReadOnly();
    }
}
