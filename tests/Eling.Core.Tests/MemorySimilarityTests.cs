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

    [Fact]
    public void CalculateSimilarity_IdenticalStrings_ReturnsOne()
    {
        var score = MemorySimilarity.CalculateSimilarity("Always check git status before commit", "Always check git status before commit");
        Assert.Equal(1.0, score, precision: 4);
    }

    [Fact]
    public void CalculateSimilarity_ExtendedVersion_ReturnsHighScore()
    {
        var score = MemorySimilarity.CalculateSimilarity(
            "Always check git status before commit",
            "Always check git status and diff before commit"
        );
        Assert.True(score >= 0.9);
    }

    [Fact]
    public void CalculateSimilarity_Paraphrase_ReturnsAboveDefaultThreshold()
    {
        var score = MemorySimilarity.CalculateSimilarity(
            "Selalu pakai question tool untuk minta approval sebelum eksekusi",
            "Selalu gunakan question tool untuk meminta approval sebelum eksekusi bash/write"
        );
        Assert.True(score > 0.7);
    }

    [Fact]
    public void CalculateSimilarity_ShortPhraseInUnrelatedLongText_ScoresLow()
    {
        var score = MemorySimilarity.CalculateSimilarity(
            "check git status",
            "Python fastapi rest endpoint configuration for user authentication and role based access control"
        );
        Assert.True(score < 0.7);
    }

    [Fact]
    public void CalculateSimilarity_CompletelyDifferent_ReturnsZeroOrLow()
    {
        var score = MemorySimilarity.CalculateSimilarity("C# dotnet build configuration", "Python fastapi rest endpoint");
        Assert.True(score < 0.1);
    }
}
