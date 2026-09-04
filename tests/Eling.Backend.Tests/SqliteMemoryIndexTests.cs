using Eling.Core;
using Microsoft.Data.Sqlite;

namespace Eling.Backend.Tests;

public class SqliteMemoryIndexTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteMemoryIndex _index;

    public SqliteMemoryIndexTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "eling_index_tests_" + Guid.NewGuid().ToString("N") + ".db");
        _index = new SqliteMemoryIndex(_dbPath);
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [Fact]
    public async Task IndexAsync_CreatesIndexedMemory()
    {
        var memory = new Memory(MemoryType.Fact, "User prefers dark mode", new[] { "ui" }, "prompt");

        await _index.IndexAsync(memory);

        var results = await _index.SearchAsync("dark");
        var hit = Assert.Single(results);
        Assert.Equal(memory.Id, hit.Id);
    }

    [Fact]
    public async Task IndexAsync_UpdatesExistingMemoryWithSameMemoryId()
    {
        var original = new Memory(MemoryType.Fact, "User prefers dark mode", new[] { "ui" }, "prompt");
        await _index.IndexAsync(original);

        var updated = new Memory(MemoryType.Fact, "User prefers light mode", new[] { "ui" }, "prompt",
            id: original.Id);
        await _index.IndexAsync(updated);

        Assert.Empty(await _index.SearchAsync("dark"));
        var results = await _index.SearchAsync("light");
        var hit = Assert.Single(results);
        Assert.Equal(original.Id, hit.Id);
    }

    [Fact]
    public async Task SearchAsync_FindsMatchingContent()
    {
        var memory = new Memory(MemoryType.Lesson, "Never trust the cache invalidation", new[] { "dev" }, "retro");

        await _index.IndexAsync(memory);

        var results = await _index.SearchAsync("cache invalidation");
        var hit = Assert.Single(results);
        Assert.Equal(memory.Id, hit.Id);
    }

    [Fact]
    public async Task SearchAsync_FindsMatchingTags()
    {
        var memory = new Memory(MemoryType.Preference, "Prefers keyboard shortcuts over mouse", new[] { "workflow", "efficiency" }, "settings");

        await _index.IndexAsync(memory);

        var results = await _index.SearchAsync("efficiency");
        var hit = Assert.Single(results);
        Assert.Equal(memory.Id, hit.Id);
    }

    [Fact]
    public async Task SearchAsync_ReturnsMemoryIdAndBm25Rank()
    {
        var lessRelevant = new Memory(MemoryType.Note, "Erlang is a functional language", new[] { "lang" }, "notes");
        var moreRelevant = new Memory(MemoryType.Note, "Erlang is a functional language and Erlang is good for concurrency and Erlang is reliable", new[] { "lang" }, "notes");
        await _index.IndexAsync(lessRelevant);
        await _index.IndexAsync(moreRelevant);

        var results = (await _index.SearchAsync("Erlang")).ToList();

        Assert.Equal(2, results.Count);
        Assert.Equal(moreRelevant.Id, results[0].Id);
        Assert.Equal(lessRelevant.Id, results[1].Id);
        Assert.True(results.All(r => r.Rank <= 0), "BM25 rank must be a real (non-positive) score.");
        Assert.All(results, r => Assert.False(double.IsNaN(r.Rank)));
    }

    [Fact]
    public async Task RemoveAsync_RemovesMemoryFromSearch()
    {
        var memory = new Memory(MemoryType.Fact, "User is on Windows", new[] { "os" }, "settings");
        await _index.IndexAsync(memory);

        await _index.RemoveAsync(memory.Id);

        Assert.Empty(await _index.SearchAsync("Windows"));
    }

    [Fact]
    public async Task RebuildAsync_IndexesMultipleMemories()
    {
        var a = new Memory(MemoryType.Fact, "The capybara is the largest rodent", new[] { "animals" }, "wiki");
        var b = new Memory(MemoryType.Fact, "The capybara is semi aquatic", new[] { "animals" }, "wiki");

        await _index.RebuildAsync(new[] { a, b });

        var results = await _index.SearchAsync("capybara");
        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Id == a.Id);
        Assert.Contains(results, r => r.Id == b.Id);
    }

    [Fact]
    public async Task RebuildAsync_RemovesStaleMemoriesNotSupplied()
    {
        var stale = new Memory(MemoryType.Fact, "Obsolete deployment procedure", new[] { "ops" }, "runbook");
        var current = new Memory(MemoryType.Fact, "Current deployment uses containers", new[] { "ops" }, "runbook");
        await _index.IndexAsync(stale);
        await _index.IndexAsync(current);

        await _index.RebuildAsync(new[] { current });

        Assert.Empty(await _index.SearchAsync("obsolete"));
        Assert.Single(await _index.SearchAsync("containers"));
    }

    [Fact]
    public async Task RebuildAsync_IsIdempotent()
    {
        var a = new Memory(MemoryType.Fact, "SQLite supports full text search", new[] { "db" }, "docs");
        var b = new Memory(MemoryType.Fact, "FTS5 is the default full text index", new[] { "db" }, "docs");

        await _index.RebuildAsync(new[] { a, b });
        await _index.RebuildAsync(new[] { a, b });

        var results = await _index.SearchAsync("full text");
        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Id == a.Id);
        Assert.Contains(results, r => r.Id == b.Id);
    }

    [Fact]
    public async Task MultipleMemories_CanCoexist()
    {
        var a = new Memory(MemoryType.Fact, "Mercury is closest to the sun", new[] { "planets" }, "wiki");
        var b = new Memory(MemoryType.Fact, "Venus is hottest planet", new[] { "planets" }, "wiki");
        var c = new Memory(MemoryType.Fact, "Earth supports life", new[] { "planets" }, "wiki");

        await _index.IndexAsync(a);
        await _index.IndexAsync(b);
        await _index.IndexAsync(c);

        Assert.Single(await _index.SearchAsync("Mercury"));
        Assert.Single(await _index.SearchAsync("Venus"));
        Assert.Single(await _index.SearchAsync("Earth"));
        Assert.Equal(3, (await _index.SearchAsync("planets")).Count);
    }

    [Fact]
    public async Task Database_CanBeDeletedRecreatedAndRebuiltFromSuppliedMemories()
    {
        var a = new Memory(MemoryType.Fact, "The database file can be recreated", new[] { "db" }, "docs");
        await _index.IndexAsync(a);

        File.Delete(_dbPath);
        var recreated = new SqliteMemoryIndex(_dbPath);

        var b = new Memory(MemoryType.Fact, "Rebuild recreates the database schema", new[] { "db" }, "docs");
        await recreated.RebuildAsync(new[] { a, b });

        Assert.Equal(2, (await recreated.SearchAsync("database")).Count);
    }

    [Fact]
    public async Task SearchAsync_ReturnsExpectedMemories_AfterRebuildOfFreshDatabase()
    {
        var expected = new Memory(MemoryType.Decision, "Standardize on FTS5 for memory search", new[] { "architecture" }, "adr");
        var other = new Memory(MemoryType.Note, "Keep dependencies minimal", new[] { "architecture" }, "notes");

        await _index.RebuildAsync(new[] { expected, other });

        var results = await _index.SearchAsync("FTS5");
        var hit = Assert.Single(results);
        Assert.Equal(expected.Id, hit.Id);
    }

    [Fact]
    public async Task Index_UsesFts5VirtualTable_NotLikeQueries()
    {
        var memory = new Memory(MemoryType.Fact, "FTS5 verification", new[] { "db" }, "docs");
        await _index.IndexAsync(memory);

        using var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT type, sql FROM sqlite_master WHERE name = 'memory_fts';";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read(), "memory_fts table must exist.");
        Assert.Equal("table", reader.GetString(0));
        Assert.Contains("fts5", reader.GetString(1), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LIKE", reader.GetString(1), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchAsync_FreeTextWithTrailingColon_DoesNotThrowAndFindsTerm()
    {
        var memory = new Memory(MemoryType.Fact, "User keeps a pet bird named koko", new[] { "pets" }, "chat");

        await _index.IndexAsync(memory);

        // Regression: an FTS5 column-filter parse of "<term>:" used to throw
        // "SQLite Error 1: no such column: bird" because free text was passed
        // to MATCH as query language.
        var results = await _index.SearchAsync("bird:");
        var hit = Assert.Single(results);
        Assert.Equal(memory.Id, hit.Id);
    }

    [Fact]
    public async Task SearchAsync_FreeTextWithColonInsidePhrase_DoesNotThrowAndFindsTerms()
    {
        var memory = new Memory(MemoryType.Fact, "sqlite error no such column bug", new[] { "sqlite" }, "chat");

        await _index.IndexAsync(memory);

        var results = await _index.SearchAsync("sqlite error: no such column");
        Assert.Contains(results, r => r.Id == memory.Id);
    }

    [Fact]
    public async Task SearchAsync_FreeTextWithHyphen_DoesNotThrowAndFindsTerm()
    {
        var memory = new Memory(MemoryType.Fact, "User is a well-known public speaker", new[] { "profile" }, "chat");

        await _index.IndexAsync(memory);

        var results = await _index.SearchAsync("well-known");
        var hit = Assert.Single(results);
        Assert.Equal(memory.Id, hit.Id);
    }

    [Fact]
    public async Task SearchAsync_FreeTextWithUnbalancedDoubleQuote_DoesNotThrowAndFindsTerms()
    {
        var memory = new Memory(MemoryType.Fact, "User might say hi now", new[] { "chat" }, "chat");

        await _index.IndexAsync(memory);

        // Unbalanced quotes open an unterminated FTS5 phrase and used to throw
        // "fts5: syntax error near ...".
        var results = await _index.SearchAsync("say \"hi");
        var hit = Assert.Single(results);
        Assert.Equal(memory.Id, hit.Id);
    }

    [Fact]
    public async Task SearchAsync_PunctuationOnlyQuery_ReturnsEmptyWithoutThrowing()
    {
        var memory = new Memory(MemoryType.Fact, "User keeps a pet bird", new[] { "pets" }, "chat");
        await _index.IndexAsync(memory);

        var results = await _index.SearchAsync(":: : -");

        Assert.Empty(results);
    }
}
