using Eling.Core.Projects;

namespace Eling.Core.Tests;

public sealed class JsonWorkspaceRegistryTests : IDisposable
{
    private readonly string _dir;

    public JsonWorkspaceRegistryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"eling-json-reg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    private string RegistryPath => Path.Combine(_dir, "agent-workspaces.json");

    private static RegisteredWorkspace NewRow(
        string root,
        string headScopeRoot,
        bool codebaseEnabled)
    {
        var now = DateTimeOffset.UtcNow;
        return new RegisteredWorkspace(
            WorkspaceId: "",
            WorkspaceRoot: root,
            HeadScopeRoot: headScopeRoot,
            CodebaseEnabled: codebaseEnabled,
            FirstSeenAt: now,
            LastSeenAt: now);
    }

    [Fact]
    public async Task Record_WhenRootIsNew_PersistsRowWithMintedIdentity()
    {
        // Arrange
        using var registry = new JsonWorkspaceRegistry(RegistryPath);
        var root = Path.Combine(_dir, "proj");

        // Act
        registry.Record(NewRow(root, root, codebaseEnabled: true));

        // Assert
        var stored = await registry.FindByRootAsync(root);
        Assert.NotNull(stored);
        Assert.Equal(root, stored!.WorkspaceRoot);
        Assert.True(stored.CodebaseEnabled);
        Assert.False(string.IsNullOrWhiteSpace(stored.WorkspaceId));
        Assert.NotEqual(default, stored.FirstSeenAt);
        Assert.NotEqual(default, stored.LastSeenAt);
    }

    [Fact]
    public async Task Record_WhenRootAlreadyExists_RefreshesRowButKeepsHistory()
    {
        // Arrange
        using var registry = new JsonWorkspaceRegistry(RegistryPath);
        var root = Path.Combine(_dir, "proj");
        var movedScope = Path.Combine(_dir, "scope");
        registry.Record(NewRow(root, root, codebaseEnabled: false));
        var first = (await registry.FindByRootAsync(root))!;

        // Act
        registry.Record(NewRow(root, movedScope, codebaseEnabled: true));

        // Assert
        var updated = (await registry.FindByRootAsync(root))!;
        Assert.Equal(first.WorkspaceId, updated.WorkspaceId);
        Assert.Equal(first.FirstSeenAt, updated.FirstSeenAt);
        Assert.Equal(movedScope, updated.HeadScopeRoot);
        Assert.True(updated.CodebaseEnabled);
        Assert.True(updated.LastSeenAt >= first.LastSeenAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Record_WhenRootIsBlank_DoesNotPersistRow(string root)
    {
        // Arrange
        using var registry = new JsonWorkspaceRegistry(RegistryPath);

        // Act
        registry.Record(NewRow(root, root, codebaseEnabled: false));

        // Assert
        Assert.Empty(await registry.ListAsync());
    }

    [Fact]
    public async Task Record_WhenRootIsUserScopeSentinel_DoesNotPersistRow()
    {
        // Arrange
        using var registry = new JsonWorkspaceRegistry(RegistryPath);

        // Act
        registry.Record(NewRow("UserScope", "UserScope", codebaseEnabled: false));

        // Assert
        Assert.Empty(await registry.ListAsync());
    }

    [Fact]
    public async Task Record_WhenRootIsRelative_DoesNotPersistRow()
    {
        // Arrange
        using var registry = new JsonWorkspaceRegistry(RegistryPath);

        // Act
        registry.Record(NewRow("relative\\root", "relative\\root", codebaseEnabled: false));

        // Assert
        Assert.Empty(await registry.ListAsync());
    }

    [Fact]
    public async Task ListAsync_WhenRowsExist_ReturnsMostRecentlySeenFirst()
    {
        // Arrange — timestamps are the store's caller-provided values, so the
        // order is asserted on data, not on clock luck between two UtcNow calls.
        using var registry = new JsonWorkspaceRegistry(RegistryPath);
        var older = Path.Combine(_dir, "older");
        var newer = Path.Combine(_dir, "newer");
        var t1 = DateTimeOffset.UtcNow;
        var t2 = t1.AddMinutes(1);
        var t3 = t1.AddMinutes(2);

        registry.Record(NewRow(older, older, codebaseEnabled: true) with { FirstSeenAt = t1, LastSeenAt = t1 });
        registry.Record(NewRow(newer, newer, codebaseEnabled: true) with { FirstSeenAt = t2, LastSeenAt = t2 });

        // Act — re-register the older folder at a later time: it should sort first.
        registry.Record(NewRow(older, older, codebaseEnabled: true) with { FirstSeenAt = t1, LastSeenAt = t3 });
        var rows = await registry.ListAsync();

        // Assert
        Assert.Equal(2, rows.Count);
        Assert.Equal(older, rows[0].WorkspaceRoot);
    }

    [Fact]
    public async Task ListAsync_WhenFileMissing_ReturnsEmpty()
    {
        // Arrange
        using var registry = new JsonWorkspaceRegistry(RegistryPath);

        // Act
        var rows = await registry.ListAsync();

        // Assert
        Assert.Empty(rows);
    }

    [Fact]
    public async Task FindByRootAsync_WhenRootUnknown_ReturnsNull()
    {
        // Arrange
        using var registry = new JsonWorkspaceRegistry(RegistryPath);

        // Act
        var found = await registry.FindByRootAsync(Path.Combine(_dir, "missing"));

        // Assert
        Assert.Null(found);
    }

    [Fact]
    public async Task DeleteByRootAsync_WhenRootKnown_RemovesRowAndReportsDeletion()
    {
        // Arrange
        using var registry = new JsonWorkspaceRegistry(RegistryPath);
        var root = Path.Combine(_dir, "proj");
        registry.Record(NewRow(root, root, codebaseEnabled: true));

        // Act
        var removed = await registry.DeleteByRootAsync(root);
        var removedAgain = await registry.DeleteByRootAsync(root);

        // Assert
        Assert.True(removed);
        Assert.False(removedAgain);
        Assert.Empty(await registry.ListAsync());
    }

    [Fact]
    public async Task DeleteMissingRootsAsync_WhenFolderIsGone_RemovesRowAndReportsCount()
    {
        // Arrange — alive is a real folder; gone points at a path that was never created.
        using var registry = new JsonWorkspaceRegistry(RegistryPath);
        var alive = Path.Combine(_dir, "alive");
        var gone = Path.Combine(_dir, "gone");
        Directory.CreateDirectory(alive);
        registry.Record(NewRow(alive, alive, codebaseEnabled: true));
        registry.Record(NewRow(gone, gone, codebaseEnabled: true));

        // Act
        var removed = await registry.DeleteMissingRootsAsync();

        // Assert
        Assert.Equal(1, removed);
        var rows = await registry.ListAsync();
        Assert.Single(rows);
        Assert.Equal(alive, rows[0].WorkspaceRoot);
    }

    [Fact]
    public async Task DeleteMissingRootsAsync_WhenEveryFolderExists_KeepsEveryRowAndReportsZero()
    {
        // Arrange
        using var registry = new JsonWorkspaceRegistry(RegistryPath);
        var first = Path.Combine(_dir, "first");
        var second = Path.Combine(_dir, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        registry.Record(NewRow(first, first, codebaseEnabled: true));
        registry.Record(NewRow(second, second, codebaseEnabled: false));

        // Act
        var removed = await registry.DeleteMissingRootsAsync();

        // Assert
        Assert.Equal(0, removed);
        Assert.Equal(2, (await registry.ListAsync()).Count);
    }

    [Fact]
    public async Task Record_PersistsAcrossRegistryInstances()
    {
        // Arrange
        var root = Path.Combine(_dir, "proj");
        using (var writer = new JsonWorkspaceRegistry(RegistryPath))
        {
            writer.Record(NewRow(root, root, codebaseEnabled: true));
        }

        // Act
        using var reader = new JsonWorkspaceRegistry(RegistryPath);
        var rows = await reader.ListAsync();

        // Assert
        Assert.Single(rows);
        Assert.Equal(root, rows[0].WorkspaceRoot);
    }

    [Fact]
    public async Task ListAsync_WhenFileIsLegacyStringArray_TreatsAsEmptyAndRewritesOnNextRecord()
    {
        // Arrange
        await File.WriteAllTextAsync(RegistryPath, """["C:\\legacy-root"]""");
        using var registry = new JsonWorkspaceRegistry(RegistryPath);

        // Act
        Assert.Empty(await registry.ListAsync());
        registry.Record(NewRow(Path.Combine(_dir, "proj"), Path.Combine(_dir, "proj"), codebaseEnabled: true));
        var rewritten = await File.ReadAllTextAsync(RegistryPath);

        // Assert
        Assert.Contains("\"workspaceRoot\"", rewritten);
        Assert.DoesNotContain("legacy-root", rewritten);
    }

    [Fact]
    public async Task ListAsync_WhenFileIsCorrupt_DoesNotThrow()
    {
        // Arrange
        await File.WriteAllTextAsync(RegistryPath, "{not-json");
        using var registry = new JsonWorkspaceRegistry(RegistryPath);

        // Act
        var rows = await registry.ListAsync();

        // Assert
        Assert.Empty(rows);
    }

    /// <summary>
    /// Two instances in one process share the single named mutex and keep every
    /// root: each record re-reads under the gate, so neither write loses the
    /// other's row. Genuine cross-process exclusion is the mutex's job and is
    /// not something a unit test can observe.
    /// </summary>
    [Fact]
    public async Task Record_FromTwoInstancesInSameProcess_KeepsEveryRoot()
    {
        // Arrange
        using var left = new JsonWorkspaceRegistry(RegistryPath);
        using var right = new JsonWorkspaceRegistry(RegistryPath);
        var a = Path.Combine(_dir, "a");
        var b = Path.Combine(_dir, "b");
        var c = Path.Combine(_dir, "c");

        // Act
        left.Record(NewRow(a, a, codebaseEnabled: true));
        right.Record(NewRow(b, b, codebaseEnabled: true));
        left.Record(NewRow(c, c, codebaseEnabled: true));

        // Assert
        using var verifier = new JsonWorkspaceRegistry(RegistryPath);
        var rows = await verifier.ListAsync();
        Assert.Equal(3, rows.Count);
        Assert.Contains(rows, r => r.WorkspaceRoot == a);
        Assert.Contains(rows, r => r.WorkspaceRoot == b);
        Assert.Contains(rows, r => r.WorkspaceRoot == c);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch
        {
        }
    }
}
