using Eling.Core.Scope;
using Eling.Core.Skill;

namespace Eling.Core.Tests;

public sealed class SkillTests
{
    [Theory]
    [InlineData("collect-user-input", true)]
    [InlineData("Skill", false)]
    [InlineData("with_underscore", false)]
    [InlineData("-leading", false)]
    [InlineData("trailing-", false)]
    [InlineData("double--dash", false)]
    [InlineData("", false)]
    public void ValidateName_enforces_kebab_case(string name, bool expected) =>
        Assert.Equal(expected, Skill.Skill.ValidateName(name));

    [Fact]
    public void ValidateName_enforces_max_length()
    {
        Assert.True(Skill.Skill.ValidateName(new string('a', Skill.Skill.MaxNameLength)));
        Assert.False(Skill.Skill.ValidateName(new string('a', Skill.Skill.MaxNameLength + 1)));
    }

    [Fact]
    public void ValidateName_null_is_rejected() =>
        Assert.False(Skill.Skill.ValidateName(null));

    [Fact]
    public void Tags_normalize_trim_lower_dedupe_hyphen_kept()
    {
        var tags = Skill.Skill.NormalizeTags(new[] { " Blazor ", "BLAZOR", "forms", "a-b" });
        Assert.Equal(new[] { "blazor", "forms", "a-b" }, tags);
    }

    [Fact]
    public void Tags_normalize_drops_short_tokens_and_splits_separators()
    {
        var tags = Skill.Skill.NormalizeTags(new[] { "a", "bc", "git_hygiene.review" });
        Assert.Equal(new[] { "bc", "git", "hygiene", "review" }, tags);
    }

    [Fact]
    public void Tags_normalize_rejects_tokens_without_letter_or_digit()
    {
        var tags = Skill.Skill.NormalizeTags(new[] { "--", "-a-", "ok-tag", "___" });

        Assert.Equal(new[] { "-a-", "ok-tag" }, tags);
    }

    [Fact]
    public void Tags_normalize_null_returns_empty() =>
        Assert.Empty(Skill.Skill.NormalizeTags(null));

    [Fact]
    public void Constructor_assigns_ulid_and_timestamps()
    {
        var s = new Skill.Skill("my-skill", "desc", "# body", null, null, null, null, null);
        Assert.False(string.IsNullOrWhiteSpace(s.Id.Value));
        Assert.True(s.CreatedAt <= s.UpdatedAt);
        Assert.Equal("eling", s.Origin.WireName);
    }

    [Fact]
    public void Constructor_keeps_body_verbatim()
    {
        var s = new Skill.Skill("my-skill", "desc", "  # body\n\n");
        Assert.Equal("  # body\n\n", s.Body);
    }

    [Fact]
    public void Constructor_rejects_invalid_name() =>
        Assert.Throws<ArgumentException>(() => new Skill.Skill("Bad Name", "desc", "body"));

    [Fact]
    public void Constructor_rejects_empty_description() =>
        Assert.Throws<ArgumentException>(() => new Skill.Skill("my-skill", "", "body"));

    [Fact]
    public void Constructor_rejects_overlong_description() =>
        Assert.Throws<ArgumentException>(
            () => new Skill.Skill("my-skill", new string('d', Skill.Skill.MaxDescriptionLength + 1), "body"));

    [Fact]
    public void Constructor_rejects_empty_body() =>
        Assert.Throws<ArgumentException>(() => new Skill.Skill("my-skill", "desc", "   "));

    [Fact]
    public void Origin_wire_names_are_lowercase()
    {
        Assert.Equal("eling", SkillOrigin.Eling.WireName);
        Assert.Equal("external", SkillOrigin.External.WireName);
    }

    [Fact]
    public void SkillId_roundtrips_strict_ulid()
    {
        var id = SkillId.NewId();
        Assert.True(SkillId.TryParse(id.Value, out var parsed));
        Assert.Equal(id, parsed);
        Assert.Equal(id.Value, SkillId.Parse(id.Value).Value);
        Assert.False(SkillId.TryParse("not-a-ulid", out _));
        Assert.Throws<ArgumentException>(() => SkillId.Parse("not-a-ulid"));
    }

    [Fact]
    public void SkillId_default_ToString_is_empty()
    {
        var id = default(SkillId);

        Assert.Equal(string.Empty, id.ToString());
        Assert.Null(id.Value);
    }

    [Fact]
    public void SkillCatalogPaths_rooted_at_agents_and_dedicated_index()
    {
        Assert.EndsWith(".agents", SkillCatalogPaths.CatalogDirectory);
        Assert.EndsWith(
            Path.Combine(".trash", "skills"),
            SkillCatalogPaths.TrashDirectory);
        Assert.Equal("skill-index.db", Path.GetFileName(SkillCatalogPaths.IndexFilePath));
        // The dedicated skill index is a sibling of memory.db in the data root,
        // alongside it — not inside the regenerable runtime/ directory.
        Assert.Equal(
            Path.GetFileName(ElingPaths.ResolveDataDir(null, null, null)),
            Path.GetFileName(Path.GetDirectoryName(SkillCatalogPaths.IndexFilePath)));
        Assert.Equal(
            Path.Combine(SkillCatalogPaths.CatalogDirectory, "skills", "my-skill"),
            SkillCatalogPaths.SkillFolder("my-skill"));
    }

    [Fact]
    public void SkillFolder_rejects_non_kebab_names() =>
        Assert.Throws<ArgumentException>(() => SkillCatalogPaths.SkillFolder("../escape"));

    [Fact]
    public void SearchResult_and_list_metadata_carry_minimal_shape()
    {
        var id = SkillId.NewId();
        var hit = new SkillSearchResult(id, 1.5, SkillOrigin.Eling, "/some/path");
        Assert.Equal(id, hit.Id);
        Assert.Equal(1.5, hit.Rank);
        Assert.Null(hit.MatchedAt);

        var metadata = new SkillListMetadata(Total: 42, Matching: 7, Limit: 10, Offset: 20);
        Assert.Equal(42, metadata.Total);
        Assert.Equal(7, metadata.Matching);
        Assert.Equal(10, metadata.Limit);
        Assert.Equal(20, metadata.Offset);
    }

    [Fact]
    public void Tags_setter_renormalizes()
    {
        var s = new Skill.Skill("my-skill", "desc", "body");
        s.Tags = new[] { " Blazor ", "BLAZOR", "forms", "a-b" };
        Assert.Equal(new[] { "blazor", "forms", "a-b" }, s.Tags);
    }

    [Fact]
    public void Mutation_setters_bump_UpdatedAt()
    {
        var past = DateTimeOffset.UtcNow.AddHours(-2);

        var byDescription = new Skill.Skill("my-skill", "desc", "body", createdAt: past, updatedAt: past);
        byDescription.Description = "new desc";
        Assert.True(byDescription.UpdatedAt > past);

        var byBody = new Skill.Skill("my-skill", "desc", "body", createdAt: past, updatedAt: past);
        byBody.Body = "new body";
        Assert.True(byBody.UpdatedAt > past);

        var byTags = new Skill.Skill("my-skill", "desc", "body", createdAt: past, updatedAt: past);
        byTags.Tags = new[] { "forms" };
        Assert.True(byTags.UpdatedAt > past);

        var bySource = new Skill.Skill("my-skill", "desc", "body", createdAt: past, updatedAt: past);
        bySource.Source = new SkillSource("https://example.invalid/upstream", Tracked: true);
        Assert.True(bySource.UpdatedAt > past);

        var byRelations = new Skill.Skill("my-skill", "desc", "body", createdAt: past, updatedAt: past);
        byRelations.RelatedMemories = new[] { new RelatedReference("01m2s5a8sc8v6q2eeqneq3js6c", "global", "reference") };
        Assert.True(byRelations.UpdatedAt > past);
    }

    [Fact]
    public void Setters_enforce_constructor_invariants()
    {
        var s = new Skill.Skill("my-skill", "desc", "body");
        Assert.Throws<ArgumentException>(() => s.Description = "");
        Assert.Throws<ArgumentException>(
            () => s.Description = new string('d', Skill.Skill.MaxDescriptionLength + 1));
        Assert.Throws<ArgumentException>(() => s.Body = "   ");
        Assert.Throws<ArgumentNullException>(() => s.Body = null!);
    }

    [Fact]
    public void Touch_bumps_UpdatedAt()
    {
        var past = DateTimeOffset.UtcNow.AddHours(-1);
        var s = new Skill.Skill("my-skill", "desc", "body", createdAt: past, updatedAt: past);
        s.Touch();
        Assert.True(s.UpdatedAt > past);
    }

    [Fact]
    public void Origin_setter_does_not_bump_UpdatedAt()
    {
        var past = DateTimeOffset.UtcNow.AddHours(-1);
        var s = new Skill.Skill("my-skill", "desc", "body", createdAt: past, updatedAt: past);
        s.Origin = SkillOrigin.External;
        Assert.Equal(past, s.UpdatedAt);
    }
}
