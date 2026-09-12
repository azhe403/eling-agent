using Eling.Core;
using Eling.Core.Memory;

namespace Eling.Core.Tests;

public class TagNormalizationTests
{
    [Fact]
    public void NormalizeTags_SplitsOnHyphen()
    {
        var result = Memory.Memory.NormalizeTags(new[] { "git-hygiene" });
        Assert.Equal(new[] { "git", "hygiene" }, result);
    }

    [Fact]
    public void NormalizeTags_SplitsOnUnderscore()
    {
        var result = Memory.Memory.NormalizeTags(new[] { "code_review" });
        Assert.Equal(new[] { "code", "review" }, result);
    }

    [Fact]
    public void NormalizeTags_SplitsOnDot()
    {
        var result = Memory.Memory.NormalizeTags(new[] { "eling.mcp" });
        Assert.Equal(new[] { "eling", "mcp" }, result);
    }

    [Fact]
    public void NormalizeTags_SplitsOnSlash()
    {
        var result = Memory.Memory.NormalizeTags(new[] { "work/worktree" });
        Assert.Equal(new[] { "work", "worktree" }, result);
    }

    [Fact]
    public void NormalizeTags_Lowercases()
    {
        var result = Memory.Memory.NormalizeTags(new[] { "GIT-Hygiene", "CODE_REVIEW" });
        Assert.Equal(new[] { "git", "hygiene", "code", "review" }, result);
    }

    [Fact]
    public void NormalizeTags_DropsTokensShorterThanTwoChars()
    {
        var result = Memory.Memory.NormalizeTags(new[] { "a", "bc", "def-g" });
        Assert.Equal(new[] { "bc", "def" }, result);
    }

    [Fact]
    public void NormalizeTags_DeduplicatesCaseInsensitive()
    {
        var result = Memory.Memory.NormalizeTags(new[] { "git", "Git", "GIT", "hygiene" });
        Assert.Equal(new[] { "git", "hygiene" }, result);
    }

    [Fact]
    public void NormalizeTags_HandlesNullInput()
    {
        var result = Memory.Memory.NormalizeTags(null);
        Assert.Empty(result);
    }

    [Fact]
    public void NormalizeTags_HandlesWhitespace()
    {
        var result = Memory.Memory.NormalizeTags(new[] { "  spaced  tag  ", "" });
        Assert.Equal(new[] { "spaced", "tag" }, result);
    }

    [Fact]
    public void NormalizeTags_HandlesMultipleSeparators()
    {
        var result = Memory.Memory.NormalizeTags(new[] { "some_tag-name.with/many" });
        Assert.Equal(new[] { "some", "tag", "name", "with", "many" }, result);
    }

    [Fact]
    public void Constructor_NormalizesTagsOnSave()
    {
        var memory = new Memory.Memory(
            MemoryType.Fact,
            "Test content",
            tags: new[] { "Git-Hygiene", "CODE_REVIEW", "a" });
        Assert.Equal(new[] { "git", "hygiene", "code", "review" }, memory.Tags);
    }

    [Fact]
    public void NormalizeTags_PreservesOrder()
    {
        var result = Memory.Memory.NormalizeTags(new[] { "zebra", "apple", "mango" });
        Assert.Equal(new[] { "zebra", "apple", "mango" }, result);
    }
}
