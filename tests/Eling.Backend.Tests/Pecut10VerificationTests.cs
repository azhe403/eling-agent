using Eling.Core;

namespace Eling.Backend.Tests;

public class Pecut10VerificationTests
{
    private static (ScopedMemoryService serviceA, ScopedMemoryService serviceB, ScopedMemoryService globalOnly, string userDir, string projA, string projB) CreateServices()
    {
        var userDir = Path.Combine(Path.GetTempPath(), "eling-verify-user-" + Guid.NewGuid().ToString("N")[..8]);
        var projA = Path.Combine(Path.GetTempPath(), "eling-projA-" + Guid.NewGuid().ToString("N")[..8]);
        var projB = Path.Combine(Path.GetTempPath(), "eling-projB-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(userDir);
        Directory.CreateDirectory(Path.Combine(projA, ".eling"));
        Directory.CreateDirectory(Path.Combine(projB, ".eling"));

        var userScope = new UserScope(userDir);
        var scopeA = new ProjectScope(projA);
        var scopeB = new ProjectScope(projB);

        var policy = new MemoryScopePolicy();
        var merger = new MemoryMerger();

        var projServiceA = new MemoryService(new FileSystemMemoryStorage(scopeA.DataDirectory), new SqliteMemoryIndex(Path.Combine(scopeA.DataDirectory, "index.db")));
        var globalServiceForA = new MemoryService(new FileSystemMemoryStorage(userScope.GlobalDataDirectory), new SqliteMemoryIndex(Path.Combine(userScope.GlobalDataDirectory, "index.db")));
        var serviceA = new ScopedMemoryService(projServiceA, globalServiceForA, policy, merger, scopeA.Root);

        var projServiceB = new MemoryService(new FileSystemMemoryStorage(scopeB.DataDirectory), new SqliteMemoryIndex(Path.Combine(scopeB.DataDirectory, "index.db")));
        var globalServiceForB = new MemoryService(new FileSystemMemoryStorage(userScope.GlobalDataDirectory), new SqliteMemoryIndex(Path.Combine(userScope.GlobalDataDirectory, "index.db")));
        var serviceB = new ScopedMemoryService(projServiceB, globalServiceForB, policy, merger, scopeB.Root);

        var dummyProj = new MemoryService(new FileSystemMemoryStorage(Path.Combine(Path.GetTempPath(), "eling-dummy-" + Guid.NewGuid().ToString("N")[..8], ".eling")), new SqliteMemoryIndex(Path.Combine(Path.GetTempPath(), "eling-dummy2-" + Guid.NewGuid().ToString("N")[..8], "index.db")));
        // For global only, use dummy project but global is userDir
        var globalOnly = new ScopedMemoryService(dummyProj, globalServiceForA, policy, merger, null);

        return (serviceA, serviceB, globalOnly, userDir, projA, projB);
    }

    [Fact]
    public async Task FinalAcceptance_ProjectA_sees_A_plus_Global_not_B()
    {
        var (serviceA, serviceB, _, userDir, projA, projB) = CreateServices();
        try
        {
            var globalMem = new Memory(MemoryType.Fact, "User prefers concise answers");
            await serviceA.SaveAsync(globalMem, "global");

            var memA = new Memory(MemoryType.Fact, "Use C#");
            await serviceA.SaveAsync(memA, "project");

            var memB = new Memory(MemoryType.Fact, "Use Python");
            await serviceB.SaveAsync(memB, "project");

            var searchA = await serviceA.SearchAsync("Use", "merged");
            Assert.Contains(searchA, r => r.Scope == MemoryScopeKind.Project);
            var globalSearch = await serviceA.SearchAsync("concise", "merged");
            Assert.Contains(globalSearch, r => r.Scope == MemoryScopeKind.Global);
            // Project A should not see Project B's memory (B is isolated)
            var allA = await serviceA.ListAsync("merged");
            Assert.DoesNotContain(allA, m => m.Memory.Content == "Use Python");

            var searchB = await serviceB.SearchAsync("Use", "merged");
            Assert.Contains(searchB, r => r.Scope == MemoryScopeKind.Project);
            Assert.DoesNotContain(await serviceB.ListAsync("merged"), m => m.Memory.Content == "Use C#");
        }
        finally
        {
            TryDelete(userDir); TryDelete(projA); TryDelete(projB);
        }
    }

    [Fact]
    public async Task ScopeIdentity_SameIdDistinctScopes()
    {
        var (serviceA, _, _, userDir, projA, projB) = CreateServices();
        try
        {
            var id = MemoryId.NewId();
            var globalMem = new Memory(MemoryType.Fact, "Global content", id: id);
            await serviceA.SaveAsync(globalMem, "global");

            var projectMem = new Memory(MemoryType.Fact, "Project content", id: id);
            await serviceA.SaveAsync(projectMem, "project");

            var globalRef = MemoryReference.ForGlobal(id);
            var projectRef = MemoryReference.ForProject(id, projA);

            var gotGlobal = await serviceA.GetByIdAsync(globalRef);
            var gotProject = await serviceA.GetByIdAsync(projectRef);

            Assert.NotNull(gotGlobal);
            Assert.NotNull(gotProject);
            Assert.Equal("Global content", gotGlobal!.Memory.Content);
            Assert.Equal("Project content", gotProject!.Memory.Content);

            // Delete global should not affect project
            await serviceA.DeleteAsync(globalRef);
            var stillProject = await serviceA.GetByIdAsync(projectRef);
            Assert.NotNull(stillProject);
            var deletedGlobal = await serviceA.GetByIdAsync(globalRef);
            Assert.Null(deletedGlobal);
        }
        finally { TryDelete(userDir); TryDelete(projA); TryDelete(projB); }
    }

    [Fact]
    public async Task WritePolicy_DefaultIsProject()
    {
        var (serviceA, _, _, userDir, projA, projB) = CreateServices();
        try
        {
            var mem = new Memory(MemoryType.Fact, "Default write");
            var saved = await serviceA.SaveAsync(mem); // no scope -> project
            Assert.Equal(MemoryScopeKind.Project, saved.Scope);

            var autoSaved = await serviceA.SaveAsync(new Memory(MemoryType.Fact, "Auto"), "auto");
            Assert.Equal(MemoryScopeKind.Project, autoSaved.Scope);

            var globalSaved = await serviceA.SaveAsync(new Memory(MemoryType.Fact, "Global"), "global");
            Assert.Equal(MemoryScopeKind.Global, globalSaved.Scope);
        }
        finally { TryDelete(userDir); TryDelete(projA); TryDelete(projB); }
    }

    [Fact]
    public async Task ProjectPriority_RanksAboveGlobal()
    {
        var (serviceA, _, _, userDir, projA, projB) = CreateServices();
        try
        {
            await serviceA.SaveAsync(new Memory(MemoryType.Fact, "Preferred language = Python"), "global");
            await serviceA.SaveAsync(new Memory(MemoryType.Fact, "Preferred language = C#"), "project");

            var results = await serviceA.SearchAsync("Preferred language", "merged");
            Assert.True(results.Count >= 2);
            var first = results.First();
            Assert.Equal(MemoryScopeKind.Project, first.Scope);
        }
        finally { TryDelete(userDir); TryDelete(projA); TryDelete(projB); }
    }

    [Fact]
    public async Task CopyPromote_SourceRemains()
    {
        var (serviceA, _, _, userDir, projA, projB) = CreateServices();
        try
        {
            var projMem = new Memory(MemoryType.Fact, "Uses PostgreSQL");
            var savedProj = await serviceA.SaveAsync(projMem, "project");
            var promoted = await serviceA.PromoteToGlobalAsync(new MemoryReference(savedProj.Id, MemoryScopeKind.Project, projA));
            Assert.NotNull(promoted);
            var stillProj = await serviceA.GetByIdAsync(new MemoryReference(savedProj.Id, MemoryScopeKind.Project, projA));
            Assert.NotNull(stillProj);

            var globalMem = new Memory(MemoryType.Fact, "Global fact");
            var savedGlobal = await serviceA.SaveAsync(globalMem, "global");
            var copied = await serviceA.CopyToProjectAsync(MemoryReference.ForGlobal(savedGlobal.Id), projA);
            Assert.NotNull(copied);
            var stillGlobal = await serviceA.GetByIdAsync(MemoryReference.ForGlobal(savedGlobal.Id));
            Assert.NotNull(stillGlobal);
        }
        finally { TryDelete(userDir); TryDelete(projA); TryDelete(projB); }
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
