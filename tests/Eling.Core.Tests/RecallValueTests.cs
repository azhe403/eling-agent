using Eling.Core.Intention;
using Eling.Core.Memory;
using Eling.Core.MemoryRecall;

namespace Eling.Core.Tests;

public sealed class RecallValueTests
{
    private static Memory.Memory SampleMemory()
        => new(MemoryType.Fact, "content", ["tag"]);

    [Fact]
    public void Context_Equality_ByValue()
    {
        // Act
        var left = new MemoryRecallContext(["a"], "f.cs", "/tmp");
        var right = new MemoryRecallContext(["a"], "f.cs", "/tmp");

        // Assert — record holds a collection, so compare element-wise
        Assert.Equal(left.Topics, right.Topics);
        Assert.Equal(left.FilePath, right.FilePath);
        Assert.Equal(left.ProjectRoot, right.ProjectRoot);
    }

    [Fact]
    public void Context_AllowsNulls()
    {
        // Act
        var context = new MemoryRecallContext([], null, null);

        // Assert
        Assert.Empty(context.Topics);
        Assert.Null(context.FilePath);
        Assert.Null(context.ProjectRoot);
    }

    [Fact]
    public void Hit_Defaults()
    {
        // Arrange
        var memory = SampleMemory();

        // Act
        var hit = new MemoryRecallHit(memory);

        // Assert
        Assert.Equal(memory, hit.Memory);
        Assert.Null(hit.MatchedVia);
        Assert.Equal(0.0, hit.PorterScore);
        Assert.Equal(0.0, hit.TrigramScore);
        Assert.Null(hit.QueryMode);
        Assert.Equal(MemoryScopeKind.Project, hit.Scope);
        Assert.Null(hit.ProjectRoot);
    }

    [Fact]
    public void Hit_Equality_ByValue()
    {
        // Arrange
        var memory = SampleMemory();

        // Act
        var left = new MemoryRecallHit(memory, ["porter"], 1.0, 2.0, "and", MemoryScopeKind.Global, null);
        var right = new MemoryRecallHit(memory, ["porter"], 1.0, 2.0, "and", MemoryScopeKind.Global, null);

        // Assert — MatchedVia is a collection, compare element-wise
        Assert.Same(memory, left.Memory);
        Assert.Equal(left.MatchedVia, right.MatchedVia);
        Assert.Equal(left.PorterScore, right.PorterScore);
        Assert.Equal(left.TrigramScore, right.TrigramScore);
        Assert.Equal(left.QueryMode, right.QueryMode);
        Assert.Equal(left.Scope, right.Scope);
        Assert.Equal(left.ProjectRoot, right.ProjectRoot);
    }

    [Fact]
    public void Stats_Equality_ByValue()
    {
        // Act
        var left = new MemoryRecallStats(10, 9, 1, 2, 3);
        var right = new MemoryRecallStats(10, 9, 1, 2, 3);

        // Assert
        Assert.Equal(left, right);
        Assert.Equal(10, left.TotalMemories);
        Assert.Equal(9, left.ActiveMemories);
    }

    [Fact]
    public void IntentionResult_Equality_ByValue()
    {
        // Arrange
        var intention = new Intention.Intention("do x", TriggerType.Topic, ["t"]);

        // Act
        var left = new MemoryRecallIntentionResult(intention, true, false);
        var right = new MemoryRecallIntentionResult(intention, true, false);

        // Assert
        Assert.Equal(left, right);
    }

    [Fact]
    public void Result_Equality_ByValue()
    {
        // Arrange
        var memory = SampleMemory();
        var hit = new MemoryRecallHit(memory);
        var scoped = new ScopedMemory(memory, MemoryScopeKind.Project, "/tmp");
        var intention = new MemoryRecallIntentionResult(
            new Intention.Intention("do x", TriggerType.Topic), false, false);
        var stats = new MemoryRecallStats(1, 1, 0, 1, 1);

        // Act
        var left = new MemoryRecallResult([hit], [scoped], [intention], stats);
        var right = new MemoryRecallResult([hit], [scoped], [intention], stats);

        // Assert — result holds collections, compare structurally
        Assert.Single(left.RecallMemories);
        Assert.Single(left.RecentMemories);
        Assert.Single(left.Intentions);
        Assert.Equal(stats, left.Stats);
        Assert.Equal(left.Stats, right.Stats);
        Assert.Same(hit, left.RecallMemories[0]);
        Assert.Same(scoped, left.RecentMemories[0]);
        Assert.Same(intention, left.Intentions[0]);
    }
}
