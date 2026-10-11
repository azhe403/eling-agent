using Eling.Core.Intention;
using Eling.Core.Memory;
using IntentionModel = Eling.Core.Intention.Intention;

namespace Eling.Core.Tests;

public sealed class IntentionValueTests
{
    [Fact]
    public void TriggerType_HasThreeValues()
    {
        // Arrange & Act
        var values = Enum.GetValues<TriggerType>();

        // Assert
        Assert.Equal(3, values.Length);
        Assert.Contains(TriggerType.Topic, values);
        Assert.Contains(TriggerType.FilePattern, values);
        Assert.Contains(TriggerType.TimeBased, values);
    }

    [Fact]
    public void Ctor_SetsDefaults()
    {
        // Act
        var intention = new IntentionModel("do x", TriggerType.Topic);

        // Assert
        Assert.Equal("do x", intention.Description);
        Assert.Equal(TriggerType.Topic, intention.TriggerType);
        Assert.Equal(MemoryStatus.Active, intention.Status);
        Assert.Null(intention.ExpiresAt);
        Assert.Null(intention.Source);
        Assert.Null(intention.Pattern);
        Assert.Empty(intention.Tags);
        Assert.True(intention.UpdatedAt >= intention.CreatedAt);
    }

    [Fact]
    public void Ctor_PreservesOptions()
    {
        // Arrange
        var created = new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);
        var expires = created.AddDays(1);

        // Act
        var intention = new IntentionModel(
            "do y",
            TriggerType.FilePattern,
            tags: ["a", "b"],
            source: "agent",
            createdAt: created,
            updatedAt: created,
            expiresAt: expires,
            pattern: "**/*.cs");

        // Assert
        Assert.Equal(created, intention.CreatedAt);
        Assert.Equal(created, intention.UpdatedAt);
        Assert.Equal(expires, intention.ExpiresAt);
        Assert.Equal("agent", intention.Source);
        Assert.Equal("**/*.cs", intention.Pattern);
        Assert.Equal(["a", "b"], intention.Tags);
    }

    [Fact]
    public void Ctor_ThrowsOnNullDescription()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new IntentionModel(null!, TriggerType.Topic));
    }

    [Fact]
    public void EachCtor_GeneratesUniqueId()
    {
        // Act
        var left = new IntentionModel("a", TriggerType.Topic);
        var right = new IntentionModel("a", TriggerType.Topic);

        // Assert
        Assert.NotEqual(left.Id, right.Id);
    }
}
