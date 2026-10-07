using Eling.Backend.Judging;
using Eling.Core.Memory;
using Eling.Core.Scope;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Eling.Backend.Tests;

/// <summary>
/// The judge prompt and its config store. Both are pure enough to test without a
/// provider, which is the point: the wording is the part that decides whether the
/// feature works at all, and it must be pinned.
/// </summary>
public sealed class JudgePromptTests
{
    /// <summary>
    /// <c>Assert.NotNull</c> does not narrow a <c>Nullable&lt;struct&gt;</c>, so unwrap
    /// explicitly rather than scattering <c>!</c> across every assertion.
    /// </summary>
    private static SemanticJudgement Required(SemanticJudgement? verdict)
    {
        Assert.NotNull(verdict);
        return verdict.GetValueOrDefault();
    }

    [Fact]
    public void Build_LabelsIncomingAsC0AndCandidatesFromC1()
    {
        var incoming = new Memory(MemoryType.Preference, "incoming text");
        var candidates = new[]
        {
            new Memory(MemoryType.Preference, "first existing"),
            new Memory(MemoryType.Preference, "second existing"),
        };

        var prompt = JudgePrompt.Build(incoming, candidates);

        Assert.Contains("label C0", prompt);
        Assert.Contains("C1:", prompt);
        Assert.Contains("C2:", prompt);
        Assert.Contains("first existing", prompt);
    }

    [Fact]
    public void Build_TruncatesVeryLongContent()
    {
        var incoming = new Memory(MemoryType.Preference, new string('x', 10_000));
        var prompt = JudgePrompt.Build(incoming, []);

        Assert.DoesNotContain(new string('x', 10_000), prompt);
        Assert.Contains(new string('x', 4000) + "...", prompt);
    }

    [Fact]
    public void Parse_ContradictionTargetingC1_MapsToTheCandidateId()
    {
        var candidates = new[]
        {
            new Memory(MemoryType.Preference, "a"),
            new Memory(MemoryType.Preference, "b"),
        };

        var verdict = Required(JudgePrompt.Parse(
            """{"relation":"contradiction","confidence":0.95,"reason":"reverses it","target":"C2"}""",
            candidates));

        Assert.Equal(SemanticRelation.Contradiction, verdict.Relation);
        Assert.Equal(candidates[1].Id, verdict.TargetId);
    }

    [Fact]
    public void Parse_TargetNone_YieldsUnrelatedWithNoTarget()
    {
        var verdict = Required(JudgePrompt.Parse(
            """{"relation":"unrelated","confidence":0.8,"reason":"different subjects","target":"none"}""",
            [new Memory(MemoryType.Preference, "a")]));

        Assert.Equal(SemanticRelation.Unrelated, verdict.Relation);
        Assert.Null(verdict.TargetId);
    }

    [Fact]
    public void Parse_TargetC0_IsRejectedBecauseC0IsTheIncomingRecord()
    {
        var verdict = Required(JudgePrompt.Parse(
            """{"relation":"revision","confidence":0.9,"reason":"self","target":"C0"}""",
            [new Memory(MemoryType.Preference, "a")]));

        Assert.Equal(SemanticRelation.Unrelated, verdict.Relation);
        Assert.Null(verdict.TargetId);
    }

    [Fact]
    public void Parse_RelationWithoutUsableTarget_DowngradesToUnrelated()
    {
        var verdict = Required(JudgePrompt.Parse(
            """{"relation":"revision","confidence":0.9,"reason":"extends it"}""",
            [new Memory(MemoryType.Preference, "a")]));

        Assert.Equal(SemanticRelation.Unrelated, verdict.Relation);
    }

    [Fact]
    public void Parse_AnswerWrappedInProse_StillExtractsTheJson()
    {
        var candidates = new[] { new Memory(MemoryType.Preference, "a") };
        var verdict = Required(JudgePrompt.Parse(
            "Here is my answer:\n```json\n{\"relation\":\"revision\",\"confidence\":0.7,\"reason\":\"extends\",\"target\":\"C1\"}\n```\nDone.",
            candidates));

        Assert.Equal(SemanticRelation.Revision, verdict.Relation);
        Assert.Equal(candidates[0].Id, verdict.TargetId);
    }

    [Fact]
    public void Parse_ConfidenceOutsideRange_IsClamped()
    {
        var candidates = new[] { new Memory(MemoryType.Preference, "a") };
        var verdict = Required(JudgePrompt.Parse(
            """{"relation":"revision","confidence":4.2,"reason":"extends","target":"C1"}""",
            candidates));

        Assert.Equal(1.0, verdict.Confidence);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("{\"relation\":\"sideways\",\"confidence\":0.5,\"reason\":\"x\",\"target\":\"C1\"}")]
    public void Parse_UnusableAnswer_ReturnsNullSoTheCallerCreatesInstead(string raw)
    {
        var candidates = new[] { new Memory(MemoryType.Preference, "a") };

        Assert.Null(JudgePrompt.Parse(raw, candidates));
    }

    [Fact]
    public void Parse_UnrelatedWithATargetNamed_StillCarriesNoTarget()
    {
        // A model can name a candidate even while saying "unrelated". Harmless in
        // production because HasTarget gates it, but the invariant belongs in the type
        // so the field can never mislead whoever reads a verdict.
        var candidates = new[] { new Memory(MemoryType.Preference, "a") };
        var verdict = Required(JudgePrompt.Parse(
            """{"relation":"unrelated","confidence":0.9,"reason":"separate topics","target":"C1"}""",
            candidates));

        Assert.Equal(SemanticRelation.Unrelated, verdict.Relation);
        Assert.Null(verdict.TargetId);
        Assert.False(verdict.HasTarget);
    }
}