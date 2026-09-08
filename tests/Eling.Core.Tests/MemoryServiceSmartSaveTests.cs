using Eling.Core;
using Xunit;

namespace Eling.Core.Tests;

public class MemoryServiceSmartSaveTests
{
    [Fact]
    public async Task SaveAsync_FuzzySimilarActiveMemory_MergesAndUpdatesExisting()
    {
        var storage = new InMemoryMemoryStorage();
        var index = new InMemoryMemoryIndex();
        var service = new MemoryService(
            storage,
            index,
            new SmartSaveOptions { DuplicateThreshold = 0.5 });

        var initial = new Memory(
            MemoryType.Preference,
            "Selalu pakai question tool untuk minta approval sebelum eksekusi",
            new[] { "workflow" });
        var firstResult = await service.SaveAsync(initial);
        Assert.Equal(SaveAction.Created, firstResult.Action);

        var incoming = new Memory(
            MemoryType.Preference,
            "Selalu gunakan question tool untuk meminta approval sebelum eksekusi bash/write",
            new[] { "preference", "approval" });
        var secondResult = await service.SaveAsync(incoming);

        Assert.Equal(SaveAction.Updated, secondResult.Action);
        Assert.Equal(firstResult.Memory.Id, secondResult.Memory.Id);
        Assert.Contains("approval", secondResult.Memory.Tags);
        Assert.Contains("workflow", secondResult.Memory.Tags);
        Assert.Equal("Selalu gunakan question tool untuk meminta approval sebelum eksekusi bash/write", secondResult.Memory.Content);
    }

    [Fact]
    public async Task SaveAsync_DifferentType_SkipsFuzzyMatch()
    {
        var storage = new InMemoryMemoryStorage();
        var index = new InMemoryMemoryIndex();
        var service = new MemoryService(storage, index);

        var initial = new Memory(MemoryType.Preference, "Always check git status before commit", new[] { "git" });
        await service.SaveAsync(initial);

        var incoming = new Memory(MemoryType.Lesson, "Always check git status before commit", new[] { "lesson" });
        var result = await service.SaveAsync(incoming);

        Assert.Equal(SaveAction.Created, result.Action);
    }

    [Fact]
    public async Task SaveAsync_DisjointContent_CreatesNewMemory()
    {
        var storage = new InMemoryMemoryStorage();
        var index = new InMemoryMemoryIndex();
        var service = new MemoryService(storage, index);

        await service.SaveAsync(new Memory(MemoryType.Preference, "Always check git status before commit", new[] { "git" }));
        var result = await service.SaveAsync(new Memory(MemoryType.Preference, "Python fastapi rest endpoint configuration", new[] { "py" }));

        Assert.Equal(SaveAction.Created, result.Action);
    }
}
