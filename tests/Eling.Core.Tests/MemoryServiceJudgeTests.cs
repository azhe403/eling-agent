using Eling.Core;
using Eling.Core.Memory;
using Eling.Core.Memory.Storage;
using Xunit;

namespace Eling.Core.Tests;

/// <summary>
/// Covers the save path once a semantic judge is wired in: the heuristic becomes a
/// retriever and the judge decides. The strings below are the real measured cases,
/// not invented ones -- their heuristic scores are asserted in the comments so a
/// metric change that alters the band shows up here.
/// </summary>
public class MemoryServiceJudgeTests
{
    // Measured with the real MemorySimilarity: 84 vs 75 tokens, 36 shared,
    // CalculateSimilarity 0.4637 -> below DuplicateThreshold 0.60, so the
    // historical path created a second memory instead of updating this one.
    private const string TwoParagraphs = "Commit message length preference (user, 2026-09-30): keep the commit description to about TWO PARAGRAPHS. Not one, not five. Shape that works: paragraph 1 = what was broken and what it caused; paragraph 2 = the fix and any non-obvious constraint someone would otherwise get wrong. The subject line stays a conventional-commit summary. Do not pad with a list of every file touched, do not add a status report about what was deliberately left out of the commit (that belongs in the chat, not in history), and do not narrate the review process. The user pushed back on an over-long commit message earlier in the same session, so treat 2 paragraphs as the default ceiling rather than a suggestion.";

    private const string OneParagraph = "Commit message length preference (user, 2026-09-30, settled after three rounds): ONE short paragraph, roughly 4-6 wrapped lines. Subject line is a conventional-commit summary; the body is a single paragraph stating what broke and how it was fixed, with no blank-line breaks, no file lists, no status report about what was left out of the commit (that belongs in chat), and no narration of the review process. The user rejected a 34-line message, then a 15-line one, then a 9-line two-paragraph version, landing on one paragraph. When in doubt, cut: a reader who needs more can read the diff.";

    // Measured CalculateSimilarity 0.7500 -- well above DuplicateThreshold, so the
    // historical path merged these two opposite preferences without recording why.
    private const string DarkMode = "User prefers dark mode";

    private const string LightMode = "User prefers light mode";

    [Fact]
    public async Task SaveAsync_JudgeEnabled_NoCandidate_CreatesWithoutCallingJudge()
    {
        var fixture = NewService();
        await fixture.Service.SaveAsync(MemoryFact("Python fastapi rest endpoint configuration"));

        var result = await fixture.Service.SaveAsync(MemoryFact("Kubernetes ingress controller timeout settings"));

        Assert.Equal(0, fixture.Judge.CallCount);
        Assert.Equal(SaveAction.Created, result.Action);
    }

    [Fact]
    public async Task SaveAsync_JudgeSaysContradiction_UpdatesInsteadOfCreating()
    {
        var fixture = NewService();
        var first = await fixture.Service.SaveAsync(MemoryPreference(TwoParagraphs));
        Assert.Equal(SaveAction.Created, first.Action);

        fixture.Judge.Verdict = new SemanticJudgement(
            SemanticRelation.Contradiction,
            0.95,
            "one paragraph replaces the two-paragraph ceiling",
            first.Id);

        var second = await fixture.Service.SaveAsync(MemoryPreference(OneParagraph));

        Assert.Equal(SaveAction.Updated, second.Action);
        Assert.Equal(first.Id, second.Id);
        Assert.Contains("judge: contradiction", second.Reason);
    }

    [Fact]
    public async Task SaveAsync_JudgeSaysContradiction_LeavesExactlyOneMemory()
    {
        var fixture = NewService();
        var first = await fixture.Service.SaveAsync(MemoryPreference(TwoParagraphs));
        fixture.Judge.Verdict = new SemanticJudgement(SemanticRelation.Contradiction, 0.9, "reverses the guidance", first.Id);

        await fixture.Service.SaveAsync(MemoryPreference(OneParagraph));

        var all = await fixture.Service.ListAllAsync();
        Assert.Single(all);
    }

    [Fact]
    public async Task SaveAsync_JudgeSaysUnrelated_CreatesAndRecordsReason()
    {
        var fixture = NewService();
        var first = await fixture.Service.SaveAsync(MemoryPreference(TwoParagraphs));
        fixture.Judge.Verdict = new SemanticJudgement(SemanticRelation.Unrelated, 0.8, "different subjects");

        var second = await fixture.Service.SaveAsync(MemoryPreference(OneParagraph));

        Assert.Equal(SaveAction.Created, second.Action);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Contains("judge: unrelated", second.Reason);
    }

    [Fact]
    public async Task SaveAsync_JudgeThrows_FallsBackToHeuristicAndNamesTheFailure()
    {
        var fixture = NewService();
        await fixture.Service.SaveAsync(MemoryPreference(TwoParagraphs));
        fixture.Judge.Failure = new InvalidOperationException("provider is not configured");

        var result = await fixture.Service.SaveAsync(MemoryPreference(OneParagraph));

        // Below DuplicateThreshold, so the heuristic also says create. What matters is
        // that the failure is reported rather than looking like an ordinary create.
        Assert.Equal(SaveAction.Created, result.Action);
        Assert.Contains("judge-failed: InvalidOperationException", result.Reason);
    }

    [Fact]
    public async Task SaveAsync_JudgeFails_StillMergesWhenTheHeuristicWasConfident()
    {
        var fixture = NewService();
        var first = await fixture.Service.SaveAsync(MemoryFact("Always check git status before commit"));
        fixture.Judge.Failure = new InvalidOperationException("provider is not configured");

        var result = await fixture.Service.SaveAsync(MemoryFact("Always check git status and diff before commit"));

        // The regression this guards: falling back to create here would turn an offline
        // judge into a complete loss of deduplication, since every confident merge would
        // become a duplicate.
        Assert.Equal(SaveAction.Updated, result.Action);
        Assert.Equal(first.Id, result.Id);
        Assert.Contains("judge-failed: InvalidOperationException", result.Reason);
        Assert.Single(await fixture.Service.ListAllAsync());
        Assert.NotNull(fixture.Storage);
    }

    [Fact]
    public async Task SaveAsync_JudgeTimesOut_FallsBackToHeuristicAndNamesTheTimeout()
    {
        var options = new SmartSaveOptions { JudgeTimeout = TimeSpan.FromMilliseconds(30) };
        var fixture = NewService(options);
        await fixture.Service.SaveAsync(MemoryPreference(TwoParagraphs));
        fixture.Judge.Delay = TimeSpan.FromSeconds(30);

        var result = await fixture.Service.SaveAsync(MemoryPreference(OneParagraph));

        Assert.Equal(SaveAction.Created, result.Action);
        Assert.Contains("judge-failed: timed out after", result.Reason);
    }

    [Fact]
    public async Task SaveAsync_JudgeTimesOut_ConfidentMatchStillMerges()
    {
        var options = new SmartSaveOptions { JudgeTimeout = TimeSpan.FromMilliseconds(30) };
        var fixture = NewService(options);
        var first = await fixture.Service.SaveAsync(MemoryFact("Always check git status before commit"));
        fixture.Judge.Delay = TimeSpan.FromSeconds(30);

        var result = await fixture.Service.SaveAsync(MemoryFact("Always check git status and diff before commit"));

        Assert.Equal(SaveAction.Updated, result.Action);
        Assert.Equal(first.Id, result.Id);
    }

    [Fact]
    public void Default_JudgeTimeout_IsThirtySeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), new SmartSaveOptions().JudgeTimeout);
    }

    [Fact]
    public async Task SaveAsync_JudgeNamesUnknownTarget_CreatesRatherThanMergingElsewhere()
    {
        var fixture = NewService();
        var first = await fixture.Service.SaveAsync(MemoryPreference(TwoParagraphs));
        fixture.Judge.Verdict = new SemanticJudgement(
            SemanticRelation.Revision,
            0.9,
            "extends the guidance",
            MemoryId.NewId());

        var result = await fixture.Service.SaveAsync(MemoryPreference(OneParagraph));

        Assert.Equal(SaveAction.Created, result.Action);
        Assert.NotEqual(first.Id, result.Id);
        Assert.Contains("vanished before merge", result.Reason);
    }

    [Fact]
    public async Task SaveAsync_OppositePreferences_RecordsTheContradiction()
    {
        var fixture = NewService();
        var first = await fixture.Service.SaveAsync(MemoryPreference(DarkMode));
        fixture.Judge.Verdict = new SemanticJudgement(
            SemanticRelation.Contradiction,
            0.98,
            "opposite colour-mode preferences, so the newer one supersedes",
            first.Id);

        var result = await fixture.Service.SaveAsync(MemoryPreference(LightMode));

        Assert.Equal(SaveAction.Updated, result.Action);
        Assert.Contains("judge: contradiction", result.Reason);
    }

    [Fact]
    public async Task SaveAsync_JudgeReceivesEveryNearMatchCandidate()
    {
        var fixture = NewService();
        var a = await fixture.Service.SaveAsync(MemoryPreference("Always check git status before commit"));
        var b = await fixture.Service.SaveAsync(MemoryPreference("Always check git status and diff before commit"));
        var c = await fixture.Service.SaveAsync(MemoryPreference("Always check git status, diff and branch before commit"));
        fixture.Judge.Verdict = new SemanticJudgement(SemanticRelation.Unrelated, 0.9, "nothing applies");

        await fixture.Service.SaveAsync(MemoryPreference("Always check git status, diff, branch and stash before commit"));

        Assert.Equal(3, fixture.Judge.CallCount);
        Assert.Contains(a.Id, fixture.Judge.LastCandidates);
        Assert.Contains(b.Id, fixture.Judge.LastCandidates);
        Assert.Contains(c.Id, fixture.Judge.LastCandidates);
    }

    [Fact]
    public async Task SaveAsync_WithoutJudge_KeepsHeuristicUpdate()
    {
        var service = new MemoryService(new InMemoryMemoryStorage(), new InMemoryMemoryIndex());
        var first = await service.SaveAsync(MemoryFact("Always check git status before commit"));

        var second = await service.SaveAsync(MemoryFact("Always check git status and diff before commit"));

        Assert.Equal(SaveAction.Updated, second.Action);
        Assert.Equal(first.Id, second.Id);
    }

    private static Memory.Memory MemoryFact(string content) => new(MemoryType.Fact, content);

    private static Memory.Memory MemoryPreference(string content) =>
        new(MemoryType.Preference, content, new[] { "commits", "git", "writing", "concision" });

    private sealed record JudgeTestFixture(
        MemoryService Service,
        InMemoryMemoryStorage Storage,
        StubJudge Judge);

    private static JudgeTestFixture NewService(SmartSaveOptions? options = null)
    {
        var storage = new InMemoryMemoryStorage();
        // Judge candidates now come from the real FTS search, so the judge tests need a
        // real index (temp file) rather than an in-memory substring fake that would only
        // match exact content substrings and would make every judge save look like "no
        // candidate".
        var indexPath = Path.Combine(Path.GetTempPath(), $"eling-judge-index-{Guid.NewGuid():N}.db");
        var index = new SqliteMemoryIndex(indexPath);
        var judge = new StubJudge();
        var service = new MemoryService(storage, index, options ?? new SmartSaveOptions(), judge);
        return new JudgeTestFixture(service, storage, judge);
    }

    private sealed class StubJudge : ISemanticJudge
    {
        public SemanticJudgement Verdict { get; set; }

        public Exception? Failure { get; set; }

        public TimeSpan Delay { get; set; } = TimeSpan.Zero;

        public int CallCount { get; private set; }

        public IReadOnlyCollection<MemoryId> LastCandidates { get; private set; } = [];

        public async Task<SemanticJudgement> JudgeAsync(Memory.Memory incoming, IReadOnlyCollection<Memory.Memory> candidates, CancellationToken cancellationToken)
        {
            CallCount++;
            LastCandidates = candidates.Select(c => c.Id).ToList();

            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            if (Failure is not null)
            {
                throw Failure;
            }

            return Verdict;
        }
    }
}
