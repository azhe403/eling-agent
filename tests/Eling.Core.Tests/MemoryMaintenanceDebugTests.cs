using Eling.Core;
using Xunit;
using Xunit.Abstractions;

namespace Eling.Core.Tests;

public class MemoryMaintenanceDebugTests
{
    private readonly ITestOutputHelper _output;

    public MemoryMaintenanceDebugTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Debug_Service_List_After_Save()
    {
        var storage = new InMemoryMemoryStorage();
        var index = new InMemoryMemoryIndex();
        var service = new MemoryService(storage, index, new SmartSaveOptions { DuplicateThreshold = 0.4 });
        var firstResult = await service.SaveAsync(new Memory(MemoryType.Preference, "Always check git status before commit", new[] { "git" }));
        var secondResult = await service.SaveAsync(new Memory(MemoryType.Preference, "Always check git status and diff before commit", new[] { "git" }));
        _output.WriteLine($"first: {firstResult.Action} {firstResult.Memory.Id.Value}");
        _output.WriteLine($"second: {secondResult.Action} {secondResult.Memory.Id.Value}");
        var all = await service.ListAllAsync();
        _output.WriteLine($"list count: {all.Count}");
        foreach (var m in all)
        {
            _output.WriteLine($"  - {m.Id.Value} status={m.Status} content={m.Content}");
        }
    }
}
