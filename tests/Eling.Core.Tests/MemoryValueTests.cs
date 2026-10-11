using Eling.Core.Memory;

namespace Eling.Core.Tests;

public sealed class MemoryValueTests
{
    [Fact]
    public void ForGlobal_SetsScope_AndNullRoot()
    {
        // Arrange
        var id = MemoryId.NewId();

        // Act
        var reference = MemoryReference.ForGlobal(id);

        // Assert
        Assert.Equal(id, reference.Id);
        Assert.Equal(MemoryScopeKind.Global, reference.Scope);
        Assert.Null(reference.ProjectRoot);
    }

    [Fact]
    public void ForProject_NormalizesToFullPath()
    {
        // Arrange
        var id = MemoryId.NewId();
        var relative = Path.Combine(".", "tmp");

        // Act
        var reference = MemoryReference.ForProject(id, relative);

        // Assert
        Assert.Equal(MemoryScopeKind.Project, reference.Scope);
        Assert.Equal(Path.GetFullPath(relative), reference.ProjectRoot);
    }

    [Fact]
    public void ForProject_ThrowsArgumentNullOnNull()
    {
        // Arrange
        var id = MemoryId.NewId();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => MemoryReference.ForProject(id, null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ForProject_ThrowsOnEmptyOrWhitespace(string root)
    {
        // Arrange
        var id = MemoryId.NewId();

        // Act & Assert
        Assert.Throws<ArgumentException>(() => MemoryReference.ForProject(id, root));
    }

    [Fact]
    public void ForProjectLocal_NormalizesToFullPath()
    {
        // Arrange
        var id = MemoryId.NewId();
        var relative = Path.Combine(".", "tmp");

        // Act
        var reference = MemoryReference.ForProjectLocal(id, relative);

        // Assert
        Assert.Equal(MemoryScopeKind.ProjectLocal, reference.Scope);
        Assert.Equal(Path.GetFullPath(relative), reference.ProjectRoot);
    }

    [Fact]
    public void ForProjectLocal_ThrowsArgumentNullOnNull()
    {
        // Arrange
        var id = MemoryId.NewId();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => MemoryReference.ForProjectLocal(id, null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ForProjectLocal_ThrowsOnEmptyOrWhitespace(string root)
    {
        // Arrange
        var id = MemoryId.NewId();

        // Act & Assert
        Assert.Throws<ArgumentException>(() => MemoryReference.ForProjectLocal(id, root));
    }

    [Fact]
    public void Reference_Equality_ByValue()
    {
        // Arrange
        var id = MemoryId.NewId();

        // Act
        var left = new MemoryReference(id, MemoryScopeKind.Global, null);
        var right = new MemoryReference(id, MemoryScopeKind.Global, null);

        // Assert
        Assert.Equal(left, right);
    }

    [Fact]
    public void NearMatch_Equality_ByValue()
    {
        // Arrange
        var id = MemoryId.NewId();
        var tags = new[] { "a", "b" };

        // Act
        var left = new MemoryNearMatch(id, "preview", 0.5, MemoryType.Fact, tags);
        var right = new MemoryNearMatch(id, "preview", 0.5, MemoryType.Fact, tags);

        // Assert
        Assert.Equal(left, right);
    }

    [Fact]
    public void SmartSave_Defaults()
    {
        // Act
        var options = new SmartSaveOptions();

        // Assert
        Assert.Equal(0.60, options.DuplicateThreshold);
        Assert.Equal(0.75, options.CrossScopeDuplicateThreshold);
        Assert.Equal(0.35, options.NearMatchThreshold);
        Assert.True(options.EnableFuzzyMatch);
        Assert.Equal(TimeSpan.FromSeconds(10), options.InitialJudgeTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), options.JudgeTimeout);
    }

    [Fact]
    public void SmartSave_JudgeTimeout_ScalesWithInitial()
    {
        // Arrange
        var options = new SmartSaveOptions { InitialJudgeTimeout = TimeSpan.FromSeconds(20) };

        // Act
        var timeout = options.JudgeTimeout;

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(55), timeout);
    }

    [Fact]
    public void SmartSave_JudgeTimeout_ExplicitSetterWins()
    {
        // Arrange
        var options = new SmartSaveOptions { JudgeTimeout = TimeSpan.FromSeconds(5) };

        // Act & Assert
        Assert.Equal(TimeSpan.FromSeconds(5), options.JudgeTimeout);
    }

    [Fact]
    public void SearchResultLevel_Equality_ByValue()
    {
        // Arrange
        var id = MemoryId.NewId();
        var results = new[] { new MemorySearchResult(id, 1.0) };

        // Act
        var left = new SearchResultLevel("/tmp/a", results);
        var right = new SearchResultLevel("/tmp/a", results);

        // Assert
        Assert.Equal(left, right);
    }

    [Fact]
    public void ScopedSearchResult_Defaults()
    {
        // Arrange
        var id = MemoryId.NewId();

        // Act
        var result = new ScopedSearchResult(id, 1.5, MemoryScopeKind.Project);

        // Assert
        Assert.Equal(id, result.Id);
        Assert.Equal(1.5, result.Rank);
        Assert.Null(result.ProjectRoot);
        Assert.Null(result.MatchedVia);
        Assert.Equal(0.0, result.PorterScore);
        Assert.Equal(0.0, result.TrigramScore);
        Assert.Null(result.QueryMode);
    }
}
