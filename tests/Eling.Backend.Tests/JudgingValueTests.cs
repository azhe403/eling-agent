using Eling.Backend.Judging;

namespace Eling.Backend.Tests;

public sealed class JudgingValueTests
{
    [Fact]
    public void Verdict_Defaults_AllNull()
    {
        // Act
        var verdict = new JudgeVerdict();

        // Assert
        Assert.Null(verdict.Relation);
        Assert.Null(verdict.Confidence);
        Assert.Null(verdict.Reason);
        Assert.Null(verdict.Target);
    }

    [Fact]
    public void Verdict_PreservesInitValues()
    {
        // Act
        var verdict = new JudgeVerdict
        {
            Relation = "duplicate",
            Confidence = 0.9,
            Reason = "Same content, different words.",
            Target = "C1"
        };

        // Assert
        Assert.Equal("duplicate", verdict.Relation);
        Assert.Equal(0.9, verdict.Confidence);
        Assert.Equal("Same content, different words.", verdict.Reason);
        Assert.Equal("C1", verdict.Target);
    }

    [Fact]
    public void Config_Defaults_DisabledWithNulls()
    {
        // Act
        var config = new SemanticJudgeConfig();

        // Assert
        Assert.False(config.Enabled);
        Assert.Null(config.BaseUrl);
        Assert.Null(config.Model);
        Assert.Null(config.ApiKey);
        Assert.Null(config.TimeoutSeconds);
    }

    [Fact]
    public void Config_PreservesInitValues()
    {
        // Act
        var config = new SemanticJudgeConfig
        {
            Enabled = true,
            BaseUrl = "https://api.example.test",
            Model = "test-model",
            ApiKey = "secret",
            TimeoutSeconds = 20
        };

        // Assert
        Assert.True(config.Enabled);
        Assert.Equal("https://api.example.test", config.BaseUrl);
        Assert.Equal("test-model", config.Model);
        Assert.Equal("secret", config.ApiKey);
        Assert.Equal(20, config.TimeoutSeconds);
    }
}
