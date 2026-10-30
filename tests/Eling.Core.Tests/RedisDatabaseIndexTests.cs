using Eling.Core.Database;

namespace Eling.Core.Tests;

/// <summary>
/// The logical database index is the one number in the Redis surface that
/// arrives from outside - a query string, a tool argument, a stored form field -
/// so it is the one that has to be refused rather than passed along. These tests
/// pin the range and the refusal text; the endpoints and tools only have to
/// return what this type says.
/// </summary>
public sealed class RedisDatabaseIndexTests
{
    [Fact]
    public void TryCreate_Zero_IsAccepted()
    {
        Assert.True(RedisDatabaseIndex.TryCreate(0, out var index));
        Assert.Equal(0, index.Value);
    }

    [Fact]
    public void TryCreate_Fifteen_IsAccepted()
    {
        Assert.True(RedisDatabaseIndex.TryCreate(15, out var index));
        Assert.Equal(15, index.Value);
    }

    [Fact]
    public void TryCreate_JustOutsideTheRange_IsRefused()
    {
        Assert.False(RedisDatabaseIndex.TryCreate(-1, out _));
        Assert.False(RedisDatabaseIndex.TryCreate(16, out _));
    }

    [Fact]
    public void TryParse_Text_ReadsTheNumber()
    {
        Assert.True(RedisDatabaseIndex.TryParse("3", out var index));
        Assert.Equal(3, index.Value);
    }

    [Fact]
    public void TryParse_NotANumber_IsRefused()
    {
        Assert.False(RedisDatabaseIndex.TryParse("abc", out _));
        Assert.False(RedisDatabaseIndex.TryParse(null, out _));
        Assert.False(RedisDatabaseIndex.TryParse(" ", out _));
    }

    [Fact]
    public void TryParse_NumberOutsideTheRange_IsRefused()
    {
        Assert.False(RedisDatabaseIndex.TryParse("99", out _));
        Assert.False(RedisDatabaseIndex.TryParse("-1", out _));
    }

    [Fact]
    public void Refusal_NamesTheRange()
    {
        Assert.Contains("0", RedisDatabaseIndex.Refusal);
        Assert.Contains("15", RedisDatabaseIndex.Refusal);
    }

    [Fact]
    public void ToString_IsTheIndexItself()
    {
        RedisDatabaseIndex.TryCreate(7, out var index);

        Assert.Equal("7", index.ToString());
    }
}
