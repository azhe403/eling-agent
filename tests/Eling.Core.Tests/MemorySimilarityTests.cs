using Eling.Core;
using Xunit;

namespace Eling.Core.Tests;

public class MemorySimilarityTests
{
    [Fact]
    public void CalculateJaccard_IdenticalStrings_ReturnsOne()
    {
        var score = MemorySimilarity.CalculateJaccard("Always check git status before commit", "Always check git status before commit");
        Assert.Equal(1.0, score, precision: 4);
    }

    [Fact]
    public void CalculateJaccard_SlightlyDifferent_ReturnsHighOverlap()
    {
        var score = MemorySimilarity.CalculateJaccard(
            "Always check git status before commit",
            "Always check git status and diff before commit"
        );
        Assert.True(score > 0.7);
    }

    [Fact]
    public void CalculateJaccard_CompletelyDifferent_ReturnsZeroOrLow()
    {
        var score = MemorySimilarity.CalculateJaccard("C# dotnet build configuration", "Python fastapi rest endpoint");
        Assert.True(score < 0.1);
    }
}
