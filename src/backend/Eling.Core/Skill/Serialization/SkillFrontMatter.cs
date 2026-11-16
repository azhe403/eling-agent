using Eling.Core.Memory.Serialization;
using Eling.Core.Skill.Storage;
using YamlDotNet.Serialization;

namespace Eling.Core.Skill.Serialization;

/// <summary>
/// The DTO shape of a SKILL.md frontmatter block, matching spec §3.2 exactly:
/// camelCase keys, <c>relatedMemories</c> items carry <c>{ id, scope, relation }</c>,
/// <c>relatedSnippets</c> items carry <c>{ id, relation }</c> (no scope). The
/// underscored naming convention is registered as the brief requires, but every
/// member is pinned with an explicit camelCase alias so the wire keys are
/// stable regardless.
/// </summary>
internal sealed class SkillFrontMatter
{
    [YamlMember(Alias = "id")]
    public string Id { get; set; } = string.Empty;

    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "description")]
    public string Description { get; set; } = string.Empty;

    [YamlMember(Alias = "tags")]
    public List<string>? Tags { get; set; }

    [YamlMember(Alias = "relatedMemories")]
    public List<SkillRelatedMemoryFrontMatter>? RelatedMemories { get; set; }

    [YamlMember(Alias = "relatedSnippets")]
    public List<SkillRelatedSnippetFrontMatter>? RelatedSnippets { get; set; }

    [YamlMember(Alias = "source")]
    public SkillSourceFrontMatter? Source { get; set; }

    [YamlMember(Alias = "createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [YamlMember(Alias = "updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class SkillRelatedMemoryFrontMatter
{
    [YamlMember(Alias = "id")]
    public string? Id { get; set; }

    [YamlMember(Alias = "scope")]
    public string? Scope { get; set; }

    [YamlMember(Alias = "relation")]
    public string? Relation { get; set; }
}

internal sealed class SkillRelatedSnippetFrontMatter
{
    [YamlMember(Alias = "id")]
    public string? Id { get; set; }

    /// <summary>
    /// Forbidden key, kept as a sentinel so a snippet reference that
    /// nonetheless carries a scope is rejected loudly instead of being
    /// silently swallowed by <c>IgnoreUnmatchedProperties</c>.
    /// </summary>
    [YamlMember(Alias = "scope")]
    public string? Scope { get; set; }

    [YamlMember(Alias = "relation")]
    public string? Relation { get; set; }
}

internal sealed class SkillSourceFrontMatter
{
    [YamlMember(Alias = "upstream")]
    public string? Upstream { get; set; }

    [YamlMember(Alias = "tracked")]
    public bool Tracked { get; set; }
}

/// <summary>
/// Reads and writes the canonical SKILL.md document:
/// <c>---\n{yaml}\n---\n{body}</c>. The body is stored verbatim; line-ending
/// normalization happens once on read (<c>\r\n</c> → <c>\n</c>). Any
/// frontmatter that violates the controlled vocabularies (scope / relation) or
/// the aggregate invariants is rejected with <see cref="SkillFrontMatterException"/>,
/// never coerced or dropped.
/// </summary>
/// <remarks>
/// No naming convention is registered: YamlDotNet applies
/// <c>UnderscoredNamingConvention</c> to the explicit <c>[YamlMember(Alias=…)]</c>
/// values as well (verified: <c>createdAt</c> serialized to <c>created_at</c>),
/// which would violate the spec's camelCase wire keys (§3.2). Every key is
/// pinned verbatim with camelCase aliases instead; <c>IgnoreUnmatchedProperties</c>
/// is kept on the deserializer as the brief requires.
/// </remarks>
internal static class SkillFileFormat
{
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithTypeConverter(new LegacyTolerantDateTimeOffsetConverter())
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .IgnoreUnmatchedProperties()
        .WithTypeConverter(new LegacyTolerantDateTimeOffsetConverter())
        .Build();

    private const string Delimiter = "---";

    /// <summary>
    /// Validates a skill's cross-references ahead of any IO. Throws
    /// <see cref="SkillFrontMatterException"/> on vocabulary violations; the
    /// other aggregate invariants are enforced by the <see cref="Skill"/> constructor.
    /// </summary>
    public static void ValidateForWrite(Skill skill)
    {
        ArgumentNullException.ThrowIfNull(skill);

        var index = 0;
        foreach (var reference in skill.RelatedMemories)
        {
            ValidateReference(reference, requireScope: true, $"relatedMemories[{index}]");
            index++;
        }

        index = 0;
        foreach (var reference in skill.RelatedSnippets)
        {
            ValidateReference(reference, requireScope: false, $"relatedSnippets[{index}]");
            index++;
        }
    }

    /// <summary>Serializes the skill to the full SKILL.md document text.</summary>
    public static string WriteDocument(Skill skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        ValidateForWrite(skill);

        var frontMatter = new SkillFrontMatter
        {
            Id = skill.Id.Value,
            Name = skill.Name,
            Description = skill.Description,
            Tags = skill.Tags.Count > 0 ? [.. skill.Tags] : null,
            RelatedMemories = skill.RelatedMemories.Count > 0
                ? [.. skill.RelatedMemories.Select(r => new SkillRelatedMemoryFrontMatter
                    {
                        Id = r.Id,
                        Scope = r.Scope,
                        Relation = r.Relation,
                    })]
                : null,
            RelatedSnippets = skill.RelatedSnippets.Count > 0
                ? [.. skill.RelatedSnippets.Select(r => new SkillRelatedSnippetFrontMatter
                    {
                        Id = r.Id,
                        Relation = r.Relation,
                    })]
                : null,
            Source = skill.Source is null
                ? null
                : new SkillSourceFrontMatter { Upstream = skill.Source.Upstream, Tracked = skill.Source.Tracked },
            CreatedAt = skill.CreatedAt,
            UpdatedAt = skill.UpdatedAt,
        };

        // YamlDotNet emits Environment.NewLine on this platform; strip to LF so the
        // canonical document is identical on every OS.
        var yaml = Serializer.Serialize(frontMatter).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();
        return $"{Delimiter}\n{yaml}\n{Delimiter}\n{skill.Body}";
    }

    /// <summary>
    /// Parses a SKILL.md document back into a <see cref="Skill"/>.
    /// <paramref name="expectedName"/> is the folder the document was read from;
    /// a frontmatter <c>name</c> that disagrees with it means the folder was
    /// renamed underneath the file (or vice versa) and is rejected rather than
    /// producing a skill whose identity lies about where it lives.
    /// </summary>
    /// <exception cref="SkillFrontMatterException">Anything unreadable, inconsistent or vocabulary-violating.</exception>
    public static Skill ParseSkill(string raw, string expectedName)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedName);

        var normalized = raw.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

        if (!normalized.StartsWith($"{Delimiter}\n", StringComparison.Ordinal))
        {
            throw new SkillFrontMatterException("Not a SKILL.md document: missing leading '---' delimiter.");
        }

        var yamlStart = Delimiter.Length + 1;
        var closing = normalized.IndexOf($"\n{Delimiter}\n", yamlStart, StringComparison.Ordinal);
        string yaml;
        string body;

        if (closing >= 0)
        {
            yaml = normalized.Substring(yamlStart, closing - yamlStart);
            body = normalized.Substring(closing + 1 + Delimiter.Length + 1);
        }
        else if (normalized.EndsWith($"\n{Delimiter}", StringComparison.Ordinal))
        {
            yaml = normalized.Substring(yamlStart, normalized.Length - ($"\n{Delimiter}".Length) - yamlStart);
            body = string.Empty;
        }
        else
        {
            throw new SkillFrontMatterException("Not a SKILL.md document: missing closing '---' delimiter.");
        }

        SkillFrontMatter? frontMatter;
        try
        {
            frontMatter = Deserializer.Deserialize<SkillFrontMatter>(yaml);
        }
        catch (Exception ex) when (ex is not SkillFrontMatterException)
        {
            throw new SkillFrontMatterException($"Invalid YAML frontmatter: {ex.Message}", ex);
        }

        if (frontMatter is null)
        {
            throw new SkillFrontMatterException("Frontmatter block is empty.");
        }

        if (!SkillId.TryParse(frontMatter.Id, out var id))
        {
            throw new SkillFrontMatterException($"Frontmatter 'id' value '{frontMatter.Id}' is not a ULID.");
        }

        if (!Skill.ValidateName(frontMatter.Name))
        {
            throw new SkillFrontMatterException(
                $"Frontmatter 'name' value '{frontMatter.Name}' is not a valid kebab-case skill name.");
        }

        if (!string.Equals(frontMatter.Name, expectedName, StringComparison.Ordinal))
        {
            throw new SkillFrontMatterException(
                $"Frontmatter name '{frontMatter.Name}' does not match folder name '{expectedName}'; the folder and the file have drifted apart.");
        }

        if (frontMatter.CreatedAt == default)
        {
            throw new SkillFrontMatterException("Frontmatter 'createdAt' is missing or unparsable.");
        }

        var updatedAt = frontMatter.UpdatedAt == default ? frontMatter.CreatedAt : frontMatter.UpdatedAt;

        var relatedMemories = new List<RelatedReference>();
        var index = 0;
        foreach (var item in frontMatter.RelatedMemories ?? [])
        {
            var where = $"relatedMemories[{index}]";
            if (!SkillVocabulary.IsMemoryScope(item.Scope))
            {
                throw new SkillFrontMatterException(
                    $"{where}: scope '{item.Scope}' is not one of {string.Join(", ", SkillVocabulary.MemoryScopes)} (a scope is required on memory references).");
            }

            relatedMemories.Add(ToReference(item.Id, item.Scope, item.Relation, where, requireScope: true));
            index++;
        }

        var relatedSnippets = new List<RelatedReference>();
        index = 0;
        foreach (var item in frontMatter.RelatedSnippets ?? [])
        {
            var where = $"relatedSnippets[{index}]";
            if (item.Scope is not null)
            {
                throw new SkillFrontMatterException(
                    $"{where}: related snippets must not carry a scope (found '{item.Scope}').");
            }

            relatedSnippets.Add(ToReference(item.Id, null, item.Relation, where, requireScope: false));
            index++;
        }

        SkillSource? source = frontMatter.Source is null
            ? null
            : new SkillSource(frontMatter.Source.Upstream, frontMatter.Source.Tracked);

        try
        {
            return new Skill(
                frontMatter.Name,
                frontMatter.Description,
                body,
                frontMatter.Tags,
                relatedMemories,
                relatedSnippets,
                source,
                id,
                SkillOrigin.Eling,
                frontMatter.CreatedAt,
                updatedAt);
        }
        catch (ArgumentException ex)
        {
            throw new SkillFrontMatterException($"Frontmatter violates the skill invariants: {ex.Message}", ex);
        }
    }

    private static RelatedReference ToReference(string? id, string? scope, string? relation, string where, bool requireScope)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new SkillFrontMatterException($"{where}: 'id' is missing.");
        }

        if (string.IsNullOrWhiteSpace(relation))
        {
            throw new SkillFrontMatterException($"{where}: 'relation' is missing.");
        }

        if (!SkillVocabulary.IsRelation(relation))
        {
            throw new SkillFrontMatterException(
                $"{where}: relation '{relation}' is not one of {string.Join(", ", SkillVocabulary.Relations)}.");
        }

        try
        {
            return new RelatedReference(id, requireScope ? scope : null, relation);
        }
        catch (ArgumentException ex)
        {
            throw new SkillFrontMatterException($"{where}: {ex.Message}", ex);
        }
    }

    private static void ValidateReference(RelatedReference reference, bool requireScope, string where)
    {
        if (requireScope && !SkillVocabulary.IsMemoryScope(reference.Scope))
        {
            throw new SkillFrontMatterException(
                $"{where}: scope '{reference.Scope}' is not one of {string.Join(", ", SkillVocabulary.MemoryScopes)} (a scope is required on memory references).");
        }

        // Snippet references must omit the scope entirely — even an explicit
        // null round-trips through the aggregate fine, so only the value is rejected.
        if (!requireScope && reference.Scope is not null)
        {
            throw new SkillFrontMatterException(
                $"{where}: related snippets must not carry a scope (found '{reference.Scope}').");
        }

        if (!SkillVocabulary.IsRelation(reference.Relation))
        {
            throw new SkillFrontMatterException(
                $"{where}: relation '{reference.Relation}' is not one of {string.Join(", ", SkillVocabulary.Relations)}.");
        }
    }
}
