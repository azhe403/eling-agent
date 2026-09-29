using Eling.Core.Codebase;

namespace Eling.Backend.Tests;

/// <summary>
/// Pins the search relaxation ladder: Porter AND, Porter OR, trigram AND,
/// trigram OR, stopping at the first pass that clears the threshold. The
/// order is the behaviour, not an implementation detail. A looser pass that
/// runs early does two kinds of damage: it lets fuzzy hits outrank exact ones,
/// and because the trigram pass scores generously it can clear the threshold
/// on its own and skip the precise Porter OR pass that should have gone first.
/// </summary>
public sealed class CodebaseSearchFallbackTests
{
    private static CodebaseIndexService ServiceFor(ICodebaseIndex index)
        => new(Path.Combine(Path.GetTempPath(), "eling-fallback-tests"), index);

    [Fact]
    public async Task PreciseAndPassThatClearsTheThreshold_StopsTheLadder()
    {
        var index = new RecordingCodebaseIndex();
        index.QueuePorter(3);

        var hits = await ServiceFor(index).SearchAsync("alpha beta", limit: 10);

        Assert.Equal(new[] { "porter:AND" }, index.Calls);
        Assert.Equal(3, hits.Count);
    }

    [Fact]
    public async Task PorterOrRuns_BeforeAnyTrigramPass()
    {
        var index = new RecordingCodebaseIndex();
        index.QueuePorter(1); // AND: one hit, below the threshold
        index.QueuePorter(3); // OR: clears it on its own

        var hits = await ServiceFor(index).SearchAsync("alpha beta", limit: 10);

        Assert.Equal(new[] { "porter:AND", "porter:OR" }, index.Calls);
        // Passes accumulate rather than replace, so the OR pass's three hits
        // join the AND pass's one.
        Assert.Equal(4, hits.Count);
    }

    [Fact]
    public async Task TrigramIsReached_OnlyAfterPorterOrFallsShort()
    {
        var index = new RecordingCodebaseIndex();
        index.QueuePorter(1); // AND
        index.QueuePorter(1); // OR: still short
        index.QueueTrigram(4); // AND: clears the threshold

        var hits = await ServiceFor(index).SearchAsync("alpha beta", limit: 10);

        Assert.Equal(new[] { "porter:AND", "porter:OR", "trigram:AND" }, index.Calls);
        Assert.NotEmpty(hits);
    }

    [Fact]
    public async Task EveryPassFallingShort_WalksTheWholeLadderInOrder()
    {
        var index = new RecordingCodebaseIndex();
        // Every pass has to contribute nothing: the count accumulates across the
        // ladder, so three single-hit passes would clear the threshold on the
        // trigram AND step and never reach the OR step.
        index.QueuePorter(0);
        index.QueuePorter(0);
        index.QueueTrigram(0);
        index.QueueTrigram(0);

        await ServiceFor(index).SearchAsync("alpha beta", limit: 10);

        Assert.Equal(
            new[] { "porter:AND", "porter:OR", "trigram:AND", "trigram:OR" },
            index.Calls);
    }

    [Fact]
    public async Task LimitBelowTheThreshold_StopsOnTheFirstPass()
    {
        // A limit of 1 can never reach a threshold of 3, so without clamping the
        // threshold to the limit every query would run all four passes to
        // return a single row.
        var index = new RecordingCodebaseIndex();
        index.QueuePorter(1);

        var hits = await ServiceFor(index).SearchAsync("alpha beta", limit: 1);

        Assert.Equal(new[] { "porter:AND" }, index.Calls);
        Assert.Single(hits);
    }
}
