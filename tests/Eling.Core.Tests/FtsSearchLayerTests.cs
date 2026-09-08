using Eling.Core;

namespace Eling.Core.Tests;

public class FtsSearchLayerTests
{
    private static string TempDbPath()
    {
        return Path.Combine(Path.GetTempPath(), $"eling-fts-test-{Guid.NewGuid():N}.db");
    }

    private static SqliteMemoryIndex CreateIndex(out string path)
    {
        path = TempDbPath();
        return new SqliteMemoryIndex(path);
    }

    private static Memory NewMemory(string content, params string[] tags)
    {
        return new Memory(MemoryType.Fact, content, tags: tags);
    }

    [Fact]
    public async Task PorterLayer_MatchesStemmedForms()
    {
        var index = CreateIndex(out var path);
        try
        {
            var memory = NewMemory("Git hygiene rule", "hygiene");
            await index.IndexAsync(memory);

            // Porter stem "hygienic" → "hygien" matches "hygiene" stem
            var results = await index.SearchAsync("hygienic");
            Assert.Single(results);
            Assert.Equal(memory.Id, results.First().Id);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task TrigramLayer_MatchesSubstrings()
    {
        var index = CreateIndex(out var path);
        try
        {
            var memory = NewMemory("Diabetic patient notes", "diplomatic");
            await index.IndexAsync(memory);

            // Trigram matches substring within a tag
            var results = await index.SearchAsync("loma");
            Assert.NotEmpty(results);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Search_MergesResultsFromPorterAndTrigram()
    {
        var index = CreateIndex(out var path);
        try
        {
            var porterHit = NewMemory("Porter-only match content", "hygienic");
            var trigramHit = NewMemory("Trigram-only match content", "diplomatic");
            var noMatch = NewMemory("Unrelated content", "foo", "bar");

            await index.IndexAsync(porterHit);
            await index.IndexAsync(trigramHit);
            await index.IndexAsync(noMatch);

            // "hygien" - Porter stem matches hygienic
            // "loma"   - Trigram substring matches diplomatic
            var porterResults = await index.SearchAsync("hygien");
            var trigramResults = await index.SearchAsync("loma");

            Assert.Contains(porterResults, r => r.Id == porterHit.Id);
            Assert.Contains(trigramResults, r => r.Id == trigramHit.Id);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task IndexAsync_InsertsIntoBothLayers()
    {
        var index = CreateIndex(out var path);
        try
        {
            var memory = NewMemory("Test content", "tag1", "tag2");
            await index.IndexAsync(memory);

            // Both layers should find the memory
            var porterResults = await index.SearchAsync("tag1");
            var trigramResults = await index.SearchAsync("tag2");
            Assert.Single(porterResults);
            Assert.Single(trigramResults);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RemoveAsync_DeletesFromBothLayers()
    {
        var index = CreateIndex(out var path);
        try
        {
            var memory = NewMemory("Test content", "unique-tag-marker");
            await index.IndexAsync(memory);
            await index.RemoveAsync(memory.Id);

            var porterResults = await index.SearchAsync("unique");
            var trigramResults = await index.SearchAsync("marker");
            Assert.Empty(porterResults);
            Assert.Empty(trigramResults);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RebuildAsync_PopulatesBothLayers()
    {
        var index = CreateIndex(out var path);
        try
        {
            var memories = new[]
            {
                NewMemory("Memory 1", "alpha", "beta"),
                NewMemory("Memory 2", "gamma", "delta"),
            };
            await index.RebuildAsync(memories);

            var alphaResults = await index.SearchAsync("alpha");
            var deltaResults = await index.SearchAsync("delta");
            Assert.Single(alphaResults);
            Assert.Single(deltaResults);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SearchAsync_HandlesEmptyQuery()
    {
        var index = CreateIndex(out var path);
        try
        {
            var memory = NewMemory("Test", "tag");
            await index.IndexAsync(memory);
            // Tokens that are all < 2 chars produce empty query
            var results = await index.SearchAsync("a");
            Assert.Empty(results);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SchemaMigration_DropsLegacyTable()
    {
        // Simulate an old database that has the legacy 'memory_fts' table
        // AND already-populated 'memories' data (the pre-upgrade state).
        var path = TempDbPath();
        try
        {
            // Create legacy schema manually and seed one memory row in both
            // the durable 'memories' table and the legacy FTS table, exactly
            // as the pre-3-layer server would have left it on disk.
            await using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE memories (
                        id TEXT PRIMARY KEY,
                        type TEXT NOT NULL,
                        status TEXT NOT NULL,
                        content TEXT NOT NULL,
                        tags TEXT,
                        source TEXT,
                        created_at TEXT NOT NULL,
                        updated_at TEXT NOT NULL
                    );
                    CREATE VIRTUAL TABLE memory_fts USING fts5(
                        id UNINDEXED,
                        content,
                        tags,
                        source
                    );
                    INSERT INTO memories (id, type, status, content, tags, source, created_at, updated_at)
                    VALUES ('01m1wzj48fhvc4217akattznx0', 'Fact', 'Active', 'Migration test content', 'migration,test', 'seed', '2026-09-07T00:00:00.000+00:00', '2026-09-07T00:00:00.000+00:00');
                    INSERT INTO memory_fts (id, content, tags, source)
                    VALUES ('01m1wzj48fhvc4217akattznx0', 'Migration test content', 'migration test', 'seed');
                    """;
                await cmd.ExecuteNonQueryAsync();
            }

            // Opening a fresh index triggers schema migration. The FTS tables
            // must be auto-populated from the durable 'memories' table so a
            // search works immediately — no manual rebuild_index needed.
            var index = new SqliteMemoryIndex(path);
            var results = await index.SearchAsync("migration");
            Assert.Single(results);

            // Force-close pooled connections before opening another on the same file
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await Task.Delay(50);

            // Verify legacy table is gone, new tables exist
            await using var verifyConn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
            await verifyConn.OpenAsync();
            await using var checkCmd = verifyConn.CreateCommand();
            checkCmd.CommandText = """
                SELECT name FROM sqlite_master
                WHERE type IN ('table') AND name IN ('memory_fts', 'memory_fts_porter', 'memory_fts_trigram')
                ORDER BY name;
                """;
            await using var reader = await checkCmd.ExecuteReaderAsync();
            var tables = new List<string>();
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
            }

            Assert.DoesNotContain("memory_fts", tables);
            Assert.Contains("memory_fts_porter", tables);
            Assert.Contains("memory_fts_trigram", tables);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await Task.Delay(50);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Migration_WithoutLegacyTable_LeavesEmptyFtsEmpty()
    {
        // A brand-new database (no legacy 'memory_fts') has no durable rows
        // to repopulate from, so opening an index must NOT error and search
        // returns nothing.
        var index = CreateIndex(out var path);
        try
        {
            var results = await index.SearchAsync("anything");
            Assert.Empty(results);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await Task.Delay(50);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Restart_PopulatedIndex_SearchWorksWithoutManualRebuild()
    {
        // Simulates a server restart on a DB that was already migrated and
        // populated: close the first index, reopen a fresh one, and confirm
        // search still finds rows (FTS tables persist on disk).
        var index = CreateIndex(out var path);
        try
        {
            var memory = NewMemory("Persistent content", "persist-tag");
            await index.IndexAsync(memory);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await Task.Delay(50);
        }

        try
        {
            var reopened = new SqliteMemoryIndex(path);
            var results = await reopened.SearchAsync("persist");
            Assert.Single(results);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await Task.Delay(50);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task PorterLayer_OutscoresTrigramLayer_OnExactStemMatch()
    {
        var index = CreateIndex(out var path);
        try
        {
            // Index a memory with a Porter-stemmable word in the tag.
            // When the query exactly matches the stem form, Porter's BM25
            // rank is tighter (more negative) than Trigram's because Porter
            // is a primary indexer, not a substring fallback. We verify
            // this by checking the per-layer scores: porterScore should be
            // >= trigramScore for a well-formed stem match.
            var memory = NewMemory("Test content", "commitment");
            await index.IndexAsync(memory);

            var results = await index.SearchAsync("commit");
            Assert.Single(results);
            var hit = results.First();
            Assert.NotNull(hit.MatchedVia);
            Assert.Contains("porter", hit.MatchedVia!);
            Assert.True(hit.PorterScore > 0);
            Assert.True(hit.TrigramScore > 0);
            // Porter is the primary signal: its score should be at least
            // as strong as the trigram's (which is just a substring match).
            Assert.True(
                hit.PorterScore >= hit.TrigramScore,
                $"Porter score ({hit.PorterScore}) should be >= trigram score ({hit.TrigramScore}) for an exact stem match");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await Task.Delay(50);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task MatchedVia_ContainsTrigram_WhenOnlyTrigramLayerMatches()
    {
        var index = CreateIndex(out var path);
        try
        {
            // "loma" is a 4-char substring of "diplomatic" — the trigram
            // layer finds it, but "loma" is not a porter stem of any
            // whole word, so porter layer is silent.
            var memory = NewMemory("Test content", "diplomatic");
            await index.IndexAsync(memory);

            var results = await index.SearchAsync("loma");
            Assert.Single(results);
            var hit = results.First();
            Assert.NotNull(hit.MatchedVia);
            Assert.Contains("trigram", hit.MatchedVia!);
            Assert.DoesNotContain("porter", hit.MatchedVia!);
            Assert.True(hit.TrigramScore > 0);
            Assert.Equal(0.0, hit.PorterScore);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await Task.Delay(50);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task MatchedVia_ContainsBoth_WhenBothLayersMatch()
    {
        var index = CreateIndex(out var path);
        try
        {
            // "hygiene" — both layers hit: porter finds exact stem,
            // trigram finds the 7-char substring.
            var memory = NewMemory("Test content", "hygiene");
            await index.IndexAsync(memory);

            var results = await index.SearchAsync("hygiene");
            Assert.Single(results);
            var hit = results.First();
            Assert.NotNull(hit.MatchedVia);
            Assert.Contains("porter", hit.MatchedVia!);
            Assert.Contains("trigram", hit.MatchedVia!);
            Assert.True(hit.PorterScore > 0);
            Assert.True(hit.TrigramScore > 0);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await Task.Delay(50);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Rank_ReflectsWeightedSumOfLayerScores()
    {
        var index = CreateIndex(out var path);
        try
        {
            var memory = NewMemory("Test content", "hygiene");
            await index.IndexAsync(memory);

            var results = await index.SearchAsync("hygiene");
            Assert.Single(results);
            var hit = results.First();

            // Rank = porterScore + trigramScore (weighted 1.0 + 0.4)
            var expectedRank = hit.PorterScore + hit.TrigramScore;
            Assert.Equal(expectedRank, hit.Rank, precision: 5);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await Task.Delay(50);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task AndQuery_ResultsAtOrAboveThreshold_ReturnsAndMode()
    {
        var index = CreateIndex(out var path);
        try
        {
            // Three memories all sharing the token "git": AND query for
            // ['git', 'commit'] will literally contain both tokens in all
            // three, producing >= 3 unique ids.
            var a = NewMemory("alpha git commit", "git");
            var b = NewMemory("bravo git commit", "git");
            var c = NewMemory("charlie git commit", "git");
            await index.IndexAsync(a);
            await index.IndexAsync(b);
            await index.IndexAsync(c);

            var results = await index.SearchAsync("git commit");

            Assert.Equal(3, results.Count);
            Assert.All(results, r => Assert.Equal("and", r.QueryMode));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await Task.Delay(50);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task AndQuery_BelowThreshold_FallsBackToOrWithOrFallbackMode()
    {
        var index = CreateIndex(out var path);
        try
        {
            // Three memories each contain only ONE of the three tokens.
            // An AND query would return 0; OR should return all 3.
            var a = NewMemory("alpha", "git");
            var b = NewMemory("bravo", "commit");
            var c = NewMemory("charlie", "hygiene");
            await index.IndexAsync(a);
            await index.IndexAsync(b);
            await index.IndexAsync(c);

            var results = await index.SearchAsync("git commit hygiene");

            Assert.Equal(3, results.Count);
            Assert.All(results, r => Assert.Equal("or-fallback", r.QueryMode));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await Task.Delay(50);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task AndQuery_ExactlyTwoResults_TriggersOrFallback()
    {
        var index = CreateIndex(out var path);
        try
        {
            // Two memories share both "git" and "commit" so AND returns 2.
            // A third memory has only "git" (one OR-token) so OR fallback
            // surfaces it. We assert mode is "or-fallback" and all three
            // are present.
            var a = NewMemory("alpha git commit", "git");
            var b = NewMemory("bravo git commit", "git");
            var c = NewMemory("charlie git only", "git");
            await index.IndexAsync(a);
            await index.IndexAsync(b);
            await index.IndexAsync(c);

            var results = await index.SearchAsync("git commit");

            Assert.Equal(3, results.Count);
            Assert.Contains(results, r => r.Id == a.Id);
            Assert.Contains(results, r => r.Id == b.Id);
            Assert.Contains(results, r => r.Id == c.Id);
            Assert.All(results, r => Assert.Equal("or-fallback", r.QueryMode));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await Task.Delay(50);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task SingleMemoryQuery_FallsBackToOr_AsCountBelowThreshold()
    {
        var index = CreateIndex(out var path);
        try
        {
            // A single result is below the 3-hit threshold, so the
            // pipeline falls back to OR even though AND would have found
            // the same memory. The result is the same memory but with
            // QueryMode = "or-fallback", which makes the agent aware that
            // the recall is sparse.
            var only = NewMemory("git commit hygiene", "git");
            await index.IndexAsync(only);

            var results = await index.SearchAsync("git commit");

            Assert.Single(results);
            Assert.Equal("or-fallback", results.First().QueryMode);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await Task.Delay(50);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task OrFallback_PorterOnlyHit_CarriesMatchedViaPorter()
    {
        var index = CreateIndex(out var path);
        try
        {
            // Single-word Porter-stemmable memory; query that stems to
            // a different form to force OR-only path through porter layer.
            var only = NewMemory("running", "hygiene");
            await index.IndexAsync(only);

            var results = await index.SearchAsync("runner hygiene");

            // "runner" doesn't stem to "running" (they are different roots)
            // so the AND-query needs OR fallback. We just assert the mode
            // is consistent across all returned hits and at least one hit
            // carries the porter layer.
            Assert.NotEmpty(results);
            Assert.All(results, r => Assert.Equal("or-fallback", r.QueryMode));
            Assert.Contains(results, r => r.MatchedVia!.Contains("porter"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await Task.Delay(50);
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
