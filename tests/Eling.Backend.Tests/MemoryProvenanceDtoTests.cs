using Eling.Backend.Dtos;
using Eling.Backend.Mcp.Tools;
using Eling.Core;
using Eling.Core.Memory;
using Eling.Core.Memory.Storage;
using Eling.Core.MemoryRecall;

namespace Eling.Backend.Tests;

/// <summary>
/// Task 7 provenance DTOs: recall/list/get/search payloads carry
/// projectName/projectRoot of the scope level a memory lives in, and the
/// write tool reports init-required when no project scope is initialized.
/// </summary>
public sealed class MemoryProvenanceDtoTests
{
    private const string RootPath = @"C:\work\acme\integrations\payments";
    private const string RootName = "payments";

    private static string? NameOf(string? root)
        => root is null ? null : Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar));

    [Fact]
    public void RecallDto_ProjectMemory_CarriesNameAndRoot()
    {
        var memory = new Memory(MemoryType.Fact, "content");
        var hit = new MemoryRecallHit(
            memory,
            MatchedVia: ["porter"],
            PorterScore: -3.0,
            TrigramScore: 0.0,
            QueryMode: "and",
            Scope: MemoryScopeKind.Project,
            ProjectRoot: RootPath);

        var dto = MemoryRecallMemory.From(hit);

        Assert.Equal("project", dto.Scope);
        Assert.Equal(RootName, dto.ProjectName);
        Assert.Equal(RootPath, dto.ProjectRoot);
    }

    [Fact]
    public void RecallDto_GlobalMemory_NullNameAndRoot()
    {
        var memory = new Memory(MemoryType.Fact, "content");
        var hit = new MemoryRecallHit(
            memory,
            MatchedVia: ["porter"],
            PorterScore: -3.0,
            TrigramScore: 0.0,
            QueryMode: "and",
            Scope: MemoryScopeKind.Global,
            ProjectRoot: null);

        var dto = MemoryRecallMemory.From(hit);

        Assert.Equal("global", dto.Scope);
        Assert.Null(dto.ProjectName);
        Assert.Null(dto.ProjectRoot);
    }

    [Fact]
    public void RecallDto_FromBareMemory_KeepsProjectScope_NullProvenance()
    {
        var memory = new Memory(MemoryType.Fact, "content");

        var dto = MemoryRecallMemory.From(memory);

        Assert.Equal("project", dto.Scope);
        Assert.Null(dto.ProjectName);
        Assert.Null(dto.ProjectRoot);
    }

    [Fact]
    public void SearchResultDto_HasProjectName()
    {
        var dto = new ScopedSearchResultDto("id", -2.5, "project", RootName, RootPath);

        Assert.Equal(RootName, dto.ProjectName);
        Assert.Equal(RootPath, dto.ProjectRoot);
    }

    [Fact]
    public void SearchResultDto_Global_HasNullProjectName()
    {
        var dto = new ScopedSearchResultDto("id", -2.5, "global", null, null);

        Assert.Null(dto.ProjectName);
        Assert.Null(dto.ProjectRoot);
    }

    [Fact]
    public async Task MemoryWriteTool_Save_Uninitialized_ReturnsInitRequired()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "eling-dummy-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var fallbackService = new MemoryService(
                new FileSystemMemoryStorage(Path.Combine(tempDir, "fallback")),
                new SqliteMemoryIndex(Path.Combine(tempDir, "fallback", "index.db")));
            var globalService = new MemoryService(
                new FileSystemMemoryStorage(Path.Combine(tempDir, "user")),
                new SqliteMemoryIndex(Path.Combine(tempDir, "user", "index.db")));
            var scoped = new ScopedMemoryService(
                [],
                globalService,
                new MemoryScopePolicy(),
                new MemoryMerger(),
                tempDir);
            var tool = new MemoryWriteTool(fallbackService, scoped, notifier: new NullMemoryChangeNotifier());

            var response = await tool.SaveAsync("some content");

            Assert.Equal("init-required", response.Action);
            Assert.True(response.InitRequired);
            Assert.Contains("memory_init_project", response.Message);
            Assert.False(Directory.Exists(Path.Combine(tempDir, ".eling")));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}