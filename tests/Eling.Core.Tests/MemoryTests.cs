using Eling.Core;

namespace Eling.Core.Tests;

public class MemoryTests
{
    [Fact]
    public void Constructor_InitializesDefaultValues()
    {
        var before = DateTimeOffset.UtcNow;
        var memory = new Memory(MemoryType.Fact, "User prefers dark mode");
        var after = DateTimeOffset.UtcNow;

        Assert.False(string.IsNullOrWhiteSpace(memory.Id.Value));
        Assert.Equal(MemoryType.Fact, memory.Type);
        Assert.Equal(MemoryStatus.Active, memory.Status);
        Assert.Equal("User prefers dark mode", memory.Content);
        Assert.Empty(memory.Tags);
        Assert.Null(memory.Source);
        Assert.InRange(memory.CreatedAt, before, after);
        Assert.Equal(memory.CreatedAt, memory.UpdatedAt);
    }

    [Fact]
    public void Constructor_SetsProvidedValues()
    {
        var id = MemoryId.NewId();
        var createdAt = DateTimeOffset.UtcNow.AddHours(-1);
        var updatedAt = DateTimeOffset.UtcNow;
        var tags = new[] { "pref", "ui" };

        var memory = new Memory(
            type: MemoryType.Preference,
            content: "Use dark theme",
            tags: tags,
            source: "user-prompt",
            status: MemoryStatus.Superseded,
            id: id,
            createdAt: createdAt,
            updatedAt: updatedAt);

        Assert.Equal(id, memory.Id);
        Assert.Equal(MemoryType.Preference, memory.Type);
        Assert.Equal(MemoryStatus.Superseded, memory.Status);
        Assert.Equal("Use dark theme", memory.Content);
        Assert.Equal(tags, memory.Tags);
        Assert.Equal("user-prompt", memory.Source);
        Assert.Equal(createdAt, memory.CreatedAt);
        Assert.Equal(updatedAt, memory.UpdatedAt);
    }

    [Fact]
    public void MemoryId_ValidatesUlidFormat()
    {
        Assert.Throws<ArgumentException>(() => new MemoryId("invalid-ulid"));
        var validId = MemoryId.NewId();
        Assert.Equal(validId.Value, MemoryId.Parse(validId.Value).Value);
    }

    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenContentIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new Memory(MemoryType.Note, null!));
    }

    [Theory]
    [InlineData(MemoryType.Fact)]
    [InlineData(MemoryType.Preference)]
    [InlineData(MemoryType.Decision)]
    [InlineData(MemoryType.Lesson)]
    [InlineData(MemoryType.Note)]
    public void Memory_SupportsAllMemoryTypes(MemoryType type)
    {
        var memory = new Memory(type, "test content");
        Assert.Equal(type, memory.Type);
    }

    [Theory]
    [InlineData(MemoryStatus.Active)]
    [InlineData(MemoryStatus.Superseded)]
    [InlineData(MemoryStatus.Archived)]
    public void Memory_SupportsAllMemoryStatuses(MemoryStatus status)
    {
        var memory = new Memory(MemoryType.Note, "test content", status: status);
        Assert.Equal(status, memory.Status);
    }
}
