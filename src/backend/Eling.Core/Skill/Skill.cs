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
/// <remarks>
/// Mutation contract: assigning any content member (<see cref="Description"/>,
/// <see cref="Body"/>, <see cref="Tags"/>, <see cref="RelatedMemories"/>,
/// <see cref="RelatedSnippets"/>, <see cref="Source"/>) re-validates and
/// re-normalizes the value and bumps <see cref="UpdatedAt"/> through
/// <see cref="Touch"/>. <see cref="Name"/> is immutable — a rename is the
/// Task-2 reconstruct flow (<c>new Skill(..., id: ..., createdAt: ...)</c>),
/// not a property set. <see cref="Origin"/> is index-owned ownership metadata
/// and deliberately does not bump <see cref="UpdatedAt"/>; rescanning external
/// skills must not dirty their content timestamps.
/// </remarks>
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

    private string _description;
    public string Description
    {
        get => _description;
        set { ValidateDescription(value); _description = value; Touch(); }
    }

    private string _body;
    public string Body
    {
        get => _body;
        set { ValidateBody(value); _body = value; Touch(); }
    }

    private IReadOnlyCollection<string> _tags;
    public IReadOnlyCollection<string> Tags
    {
        get => _tags;
        set { _tags = NormalizeTags(value); Touch(); }
    }

    private IReadOnlyCollection<RelatedReference> _relatedMemories;
    public IReadOnlyCollection<RelatedReference> RelatedMemories
    {
        get => _relatedMemories;
        set { _relatedMemories = CopyReferences(value); Touch(); }
    }

    private IReadOnlyCollection<RelatedReference> _relatedSnippets;
    public IReadOnlyCollection<RelatedReference> RelatedSnippets
    {
        get => _relatedSnippets;
        set { _relatedSnippets = CopyReferences(value); Touch(); }
    }

    private SkillSource? _source;
    public SkillSource? Source
    {
        get => _source;
        set { _source = value; Touch(); }
    }

    /// <summary>Index-owned ownership metadata; setting it does not bump <see cref="UpdatedAt"/>.</summary>
    public SkillOrigin Origin { get; set; }

    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; private set; }

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

        ValidateDescription(description);
        ValidateBody(body);

        Id = id ?? SkillId.NewId();
        Name = name;
        // Body is stored verbatim; only emptiness is validated.
        _description = description;
        _body = body;
        _tags = NormalizeTags(tags);
        _relatedMemories = CopyReferences(relatedMemories);
        _relatedSnippets = CopyReferences(relatedSnippets);
        _source = source;
        Origin = origin;
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow;
        UpdatedAt = updatedAt ?? CreatedAt;
    }

    /// <summary>
    /// Bumps <see cref="UpdatedAt"/> to now. Called by every content mutation
    /// setter; call it directly when a change does not flow through one of
    /// those setters (e.g. a bulk rewrite applied after construction).
    /// </summary>
    public void Touch() => UpdatedAt = DateTimeOffset.UtcNow;

    private static void ValidateDescription(string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (description.Length > MaxDescriptionLength)
        {
            throw new ArgumentException(
                $"Skill description must be at most {MaxDescriptionLength} characters.",
                nameof(description));
        }
    }

    private static void ValidateBody(string body) => ArgumentException.ThrowIfNullOrWhiteSpace(body);

    private static IReadOnlyCollection<RelatedReference> CopyReferences(IEnumerable<RelatedReference>? references) =>
        references?.ToList().AsReadOnly() ?? (IReadOnlyCollection<RelatedReference>)Array.Empty<RelatedReference>();

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
