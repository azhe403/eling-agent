namespace Eling.Core.Tests;

using Eling.Core.Docs;
using Eling.Core.Docs.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

public class SqliteDocsIndexTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteDocsIndex _index;

    public SqliteDocsIndexTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"eling-docs-test-{Guid.NewGuid():N}.db");
        _index = new SqliteDocsIndex(_dbPath);
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
    }

    private static Doc CreateTestDoc(
        DocOwnership ownership = DocOwnership.Managed,
        DocOrigin origin = DocOrigin.Current,
        DocKind kind = DocKind.Spec,
        string relPath = "docs/specs/test.md",
        string title = "Test Spec",
        DocStatusValue? status = null,
        string topic = "specs",
        int order = 1,
        string? supersededBy = null,
        int stepsTotal = 0,
        int stepsDone = 0)
    {
        var id = DocId.Create(ownership, origin, relPath);
        return new Doc(
            id, ownership, origin, relPath, kind, title,
            status ?? DocStatusValue.DefaultFor(kind),
            topic, order, supersededBy, stepsTotal, stepsDone,
            "test-hash", DateTimeOffset.UtcNow);
    }

    private static DocSection CreateSection(
        string anchor = "section-1",
        string heading = "Section 1",
        int level = 1,
        int lineStart = 1,
        int lineEnd = 10,
        DocStatusValue? status = null,
        string? annotation = null)
    {
        var id = DocId.Create(DocOwnership.Managed, DocOrigin.Current, "test.md");
        return new DocSection(id, anchor, heading, level, lineStart, lineEnd,
            status ?? DocStatusValue.DefaultFor(DocKind.Spec), annotation);
    }

    private static DocRef CreateRef(
        string anchor = "section-1",
        string rawText = "`SomeClass`",
        ReferenceKind refKind = ReferenceKind.Symbol,
        string? resolvedPath = null,
        int? resolvedLine = null,
        bool resolved = false)
    {
        var id = DocId.Create(DocOwnership.Managed, DocOrigin.Current, "test.md");
        return new DocRef(id, anchor, rawText, refKind, resolvedPath, resolvedLine, resolved);
    }

    private static DocRelation CreateRelation(
        DocOwnership ownership = DocOwnership.Managed,
        DocOrigin origin = DocOrigin.Current,
        string sourcePath = "docs/specs/spec.md",
        string targetPath = "docs/plans/plan.md",
        RelationKind kind = RelationKind.Implements)
    {
        var sourceId = DocId.Create(ownership, origin, sourcePath);
        var targetId = DocId.Create(ownership, origin, targetPath);
        return new DocRelation(sourceId, targetId, kind);
    }

    [Fact]
    public async Task IndexAsync_InsertsDocWithSectionsAndRefs()
    {
        var doc = CreateTestDoc();
        var sections = new[] { CreateSection() };
        var refs = new[] { CreateRef() };

        await _index.IndexAsync(doc, sections, refs);

        var retrieved = await _index.GetAsync(doc.Id.Value);
        Assert.NotNull(retrieved);
        Assert.Equal(doc.Title, retrieved!.Title);
        Assert.Equal(doc.Kind, retrieved.Kind);
        Assert.Equal(doc.Ownership, retrieved.Ownership);
        Assert.Equal(doc.Origin, retrieved.Origin);
        Assert.Equal(doc.RelPath, retrieved.RelPath);
        Assert.Equal(doc.Topic, retrieved.Topic);
        Assert.Equal(doc.Order, retrieved.Order);
        Assert.Equal(doc.StepsTotal, retrieved.StepsTotal);
        Assert.Equal(doc.StepsDone, retrieved.StepsDone);

        var retrievedSections = await _index.GetSectionsAsync(doc.Id.Value);
        Assert.Single(retrievedSections);
        Assert.Equal(sections[0].Anchor, retrievedSections[0].Anchor);
        Assert.Equal(sections[0].Heading, retrievedSections[0].Heading);

        var retrievedRefs = await _index.GetRefsAsync(doc.Id.Value);
        Assert.Single(retrievedRefs);
        Assert.Equal(refs[0].RawText, retrievedRefs[0].RawText);
    }

    [Fact]
    public async Task IndexAsync_UpdateExistingDoc_ReplacesContent()
    {
        var doc = CreateTestDoc(title: "Original Title");
        var sections = new[] { CreateSection(heading: "Original Section") };
        await _index.IndexAsync(doc, sections, []);

        var updatedDoc = CreateTestDoc(title: "Updated Title");
        var updatedSections = new[] { CreateSection(heading: "Updated Section") };
        await _index.IndexAsync(updatedDoc, updatedSections, []);

        var retrieved = await _index.GetAsync(doc.Id.Value);
        Assert.NotNull(retrieved);
        Assert.Equal("Updated Title", retrieved!.Title);

        var retrievedSections = await _index.GetSectionsAsync(doc.Id.Value);
        Assert.Single(retrievedSections);
        Assert.Equal("Updated Section", retrievedSections[0].Heading);
    }

    [Fact]
    public async Task DeleteAsync_RemovesDocAndCascades()
    {
        var doc = CreateTestDoc();
        var sections = new[] { CreateSection() };
        var refs = new[] { CreateRef() };
        await _index.IndexAsync(doc, sections, refs);

        await _index.DeleteAsync(doc.Id.Value);

        var retrieved = await _index.GetAsync(doc.Id.Value);
        Assert.Null(retrieved);

        var retrievedSections = await _index.GetSectionsAsync(doc.Id.Value);
        Assert.Empty(retrievedSections);

        var retrievedRefs = await _index.GetRefsAsync(doc.Id.Value);
        Assert.Empty(retrievedRefs);
    }

    [Fact]
    public async Task GetSectionAsync_ReturnsSpecificSection()
    {
        var doc = CreateTestDoc();
        var sections = new[]
        {
            CreateSection(anchor: "section-1", heading: "Section 1"),
            CreateSection(anchor: "section-2", heading: "Section 2")
        };
        await _index.IndexAsync(doc, sections, []);

        var section = await _index.GetSectionAsync(doc.Id.Value, "section-2");
        Assert.NotNull(section);
        Assert.Equal("section-2", section!.Anchor);
        Assert.Equal("Section 2", section.Heading);
    }

    [Fact]
    public async Task GetSectionAsync_UnknownAnchor_ReturnsNull()
    {
        var doc = CreateTestDoc();
        await _index.IndexAsync(doc, [CreateSection()], []);

        var section = await _index.GetSectionAsync(doc.Id.Value, "nonexistent");
        Assert.Null(section);
    }

    [Fact]
    public async Task GetSectionsAsync_ReturnsOrderedByLineStart()
    {
        var doc = CreateTestDoc();
        var sections = new[]
        {
            CreateSection(anchor: "section-3", heading: "Section 3", lineStart: 30, lineEnd: 40),
            CreateSection(anchor: "section-1", heading: "Section 1", lineStart: 10, lineEnd: 20),
            CreateSection(anchor: "section-2", heading: "Section 2", lineStart: 20, lineEnd: 30)
        };
        await _index.IndexAsync(doc, sections, []);

        var retrieved = await _index.GetSectionsAsync(doc.Id.Value);
        Assert.Equal(3, retrieved.Count);
        Assert.Equal("section-1", retrieved[0].Anchor);
        Assert.Equal("section-2", retrieved[1].Anchor);
        Assert.Equal("section-3", retrieved[2].Anchor);
    }

    [Fact]
    public async Task RebuildAsync_FromEmpty_ProducesSameData()
    {
        var doc1 = CreateTestDoc(relPath: "docs/specs/spec1.md", title: "Spec 1");
        var doc2 = CreateTestDoc(relPath: "docs/plans/plan1.md", title: "Plan 1", kind: DocKind.Plan);
        var sections1 = new[] { CreateSection(anchor: "s1", heading: "Section 1") };
        var sections2 = new[] { CreateSection(anchor: "s2", heading: "Section 2") };

        await _index.IndexAsync(doc1, sections1, []);
        await _index.IndexAsync(doc2, sections2, []);

        var rebuildItems = new[]
        {
            new DocRebuildItem(doc1, sections1, Array.Empty<DocRef>()),
            new DocRebuildItem(doc2, sections2, Array.Empty<DocRef>())
        };

        await _index.RebuildAsync(rebuildItems);

        var list = await _index.ListAsync();
        Assert.Equal(2, list.Count);

        var retrieved1 = await _index.GetAsync(doc1.Id.Value);
        Assert.NotNull(retrieved1);
        Assert.Equal("Spec 1", retrieved1!.Title);

        var retrieved2 = await _index.GetAsync(doc2.Id.Value);
        Assert.NotNull(retrieved2);
        Assert.Equal("Plan 1", retrieved2!.Title);
    }

    [Fact]
    public async Task SearchAsync_FtsPorter_AndQuery()
    {
        var doc = CreateTestDoc(title: "Memory Management Spec");
        var sections = new[] { CreateSection(heading: "Memory Allocation", anchor: "alloc") };
        await _index.IndexAsync(doc, sections, []);

        var results = await _index.SearchAsync("memory allocation");
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.Title == "Memory Management Spec");
    }

    [Fact]
    public async Task SearchAsync_FtsPorter_OrFallback()
    {
        var doc1 = CreateTestDoc(relPath: "docs/specs/a.md", title: "Alpha");
        var doc2 = CreateTestDoc(relPath: "docs/specs/b.md", title: "Beta");
        await _index.IndexAsync(doc1, [CreateSection(heading: "Alpha")], []);
        await _index.IndexAsync(doc2, [CreateSection(heading: "Beta")], []);

        var results = await _index.SearchAsync("alpha beta");
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task SearchAsync_TrigramFallback_BroaderRecall()
    {
        var doc = CreateTestDoc(title: "Specialization");
        await _index.IndexAsync(doc, [CreateSection(heading: "Specialize")], []);

        var results = await _index.SearchAsync("special");
        Assert.NotEmpty(results);
    }

    [Fact]
    public async Task SearchAsync_FiltersByOwnership()
    {
        var managedDoc = CreateTestDoc(ownership: DocOwnership.Managed, relPath: "test1.md");
        var externalDoc = CreateTestDoc(ownership: DocOwnership.External, relPath: "test2.md");
        await _index.IndexAsync(managedDoc, [CreateSection()], []);
        await _index.IndexAsync(externalDoc, [CreateSection()], []);

        var managedResults = await _index.SearchAsync("test", ownership: DocOwnership.Managed);
        Assert.Single(managedResults);
        Assert.Equal(DocOwnership.Managed, managedResults[0].Ownership);

        var externalResults = await _index.SearchAsync("test", ownership: DocOwnership.External);
        Assert.Single(externalResults);
        Assert.Equal(DocOwnership.External, externalResults[0].Ownership);
    }

    [Fact]
    public async Task SearchAsync_FiltersByOrigin()
    {
        var currentDoc = CreateTestDoc(origin: DocOrigin.Current, relPath: "test1.md");
        var shardDoc = CreateTestDoc(origin: DocOrigin.Shard, relPath: "test2.md");
        await _index.IndexAsync(currentDoc, [CreateSection()], []);
        await _index.IndexAsync(shardDoc, [CreateSection()], []);

        var currentResults = await _index.SearchAsync("test", origin: DocOrigin.Current);
        Assert.Single(currentResults);
        Assert.Equal(DocOrigin.Current, currentResults[0].Origin);
    }

    [Fact]
    public async Task SearchAsync_FiltersByKind()
    {
        var spec = CreateTestDoc(kind: DocKind.Spec, relPath: "test1.md");
        var plan = CreateTestDoc(kind: DocKind.Plan, relPath: "test2.md");
        await _index.IndexAsync(spec, [CreateSection()], []);
        await _index.IndexAsync(plan, [CreateSection()], []);

        var specResults = await _index.SearchAsync("test", kind: DocKind.Spec);
        Assert.Single(specResults);
        Assert.Equal(DocKind.Spec, specResults[0].Kind);
    }

    [Fact]
    public async Task SearchAsync_FiltersByTopic()
    {
        var spec1 = CreateTestDoc(topic: "memory", relPath: "test1.md");
        var spec2 = CreateTestDoc(topic: "sync", relPath: "test2.md");
        await _index.IndexAsync(spec1, [CreateSection()], []);
        await _index.IndexAsync(spec2, [CreateSection()], []);

        var memoryResults = await _index.SearchAsync("test", topic: "memory");
        Assert.Single(memoryResults);
        Assert.Equal("memory", memoryResults[0].Topic);
    }

    [Fact]
    public async Task SearchAsync_FiltersByPathPrefix()
    {
        var doc1 = CreateTestDoc(relPath: "docs/specs/spec1.md");
        var doc2 = CreateTestDoc(relPath: "docs/plans/plan1.md");
        await _index.IndexAsync(doc1, [CreateSection()], []);
        await _index.IndexAsync(doc2, [CreateSection()], []);

        var specsResults = await _index.SearchAsync("spec", pathPrefix: "docs/specs");
        Assert.Single(specsResults);
        Assert.Contains("docs/specs", specsResults[0].RelPath);
    }

    [Fact]
    public async Task SearchAsync_EmptyQuery_ReturnsEmpty()
    {
        var results = await _index.SearchAsync("");
        Assert.Empty(results);

        results = await _index.SearchAsync("   ");
        Assert.Empty(results);
    }

    [Fact]
    public async Task ListAsync_NoFilters_ReturnsAll()
    {
        var doc1 = CreateTestDoc(relPath: "a.md", title: "A");
        var doc2 = CreateTestDoc(relPath: "b.md", title: "B");
        await _index.IndexAsync(doc1, [CreateSection()], []);
        await _index.IndexAsync(doc2, [CreateSection()], []);

        var list = await _index.ListAsync();
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public async Task ListAsync_SortByTopic_OrdersCorrectly()
    {
        var doc1 = CreateTestDoc(topic: "zzz", relPath: "a.md");
        var doc2 = CreateTestDoc(topic: "aaa", relPath: "b.md");
        await _index.IndexAsync(doc1, [CreateSection()], []);
        await _index.IndexAsync(doc2, [CreateSection()], []);

        var list = await _index.ListAsync(sort: "topic");
        Assert.Equal("aaa", list[0].Topic);
        Assert.Equal("zzz", list[1].Topic);
    }

    [Fact]
    public async Task ListAsync_SortByTitle_OrdersCorrectly()
    {
        var doc1 = CreateTestDoc(title: "Zebra", relPath: "a.md");
        var doc2 = CreateTestDoc(title: "Alpha", relPath: "b.md");
        await _index.IndexAsync(doc1, [CreateSection()], []);
        await _index.IndexAsync(doc2, [CreateSection()], []);

        var list = await _index.ListAsync(sort: "title");
        Assert.Equal("Alpha", list[0].Title);
        Assert.Equal("Zebra", list[1].Title);
    }

    [Fact]
    public async Task ListAsync_SortByUpdated_OrdersByIndexedAtDesc()
    {
        var doc1 = CreateTestDoc(title: "First", relPath: "a.md");
        await _index.IndexAsync(doc1, [CreateSection()], []);
        await Task.Delay(10); // Ensure different timestamps
        var doc2 = CreateTestDoc(title: "Second", relPath: "b.md");
        await _index.IndexAsync(doc2, [CreateSection()], []);

        var list = await _index.ListAsync(sort: "updated");
        Assert.Equal("Second", list[0].Title);
        Assert.Equal("First", list[1].Title);
    }

    [Fact]
    public async Task UpsertRelationAsync_CreatesRelation()
    {
        var doc1 = CreateTestDoc(relPath: "spec.md");
        var doc2 = CreateTestDoc(relPath: "plan.md", kind: DocKind.Plan);
        await _index.IndexAsync(doc1, [CreateSection()], []);
        await _index.IndexAsync(doc2, [CreateSection()], []);

        var relation = CreateRelation(sourcePath: "spec.md", targetPath: "plan.md", kind: RelationKind.Implements);
        await _index.UpsertRelationAsync(relation);

        var relations = await _index.GetRelationsAsync(doc1.Id.Value, asSource: true);
        Assert.Single(relations);
        Assert.Equal(RelationKind.Implements, relations[0].Kind);
        Assert.Equal(doc2.Id.Value, relations[0].TargetId.Value);
    }

    [Fact]
    public async Task UpsertRelationAsync_UpdatesExisting()
    {
        var doc1 = CreateTestDoc(relPath: "spec.md");
        var doc2 = CreateTestDoc(relPath: "plan.md", kind: DocKind.Plan);
        await _index.IndexAsync(doc1, [CreateSection()], []);
        await _index.IndexAsync(doc2, [CreateSection()], []);

        // Insert two different relation kinds - they should both exist since PK is (source, target, kind)
        await _index.UpsertRelationAsync(CreateRelation(sourcePath: "spec.md", targetPath: "plan.md", kind: RelationKind.References));
        await _index.UpsertRelationAsync(CreateRelation(sourcePath: "spec.md", targetPath: "plan.md", kind: RelationKind.Implements));

        var relations = await _index.GetRelationsAsync(doc1.Id.Value, asSource: true);
        Assert.Equal(2, relations.Count);
        Assert.Contains(relations, r => r.Kind == RelationKind.References);
        Assert.Contains(relations, r => r.Kind == RelationKind.Implements);

        // Now update the Implements relation (same kind) - should replace
        await _index.UpsertRelationAsync(CreateRelation(sourcePath: "spec.md", targetPath: "plan.md", kind: RelationKind.Implements));
        relations = await _index.GetRelationsAsync(doc1.Id.Value, asSource: true);
        Assert.Equal(2, relations.Count); // Still 2 (References + Implements)
    }

    [Fact]
    public async Task DeleteRelationAsync_RemovesRelation()
    {
        var doc1 = CreateTestDoc(relPath: "spec.md");
        var doc2 = CreateTestDoc(relPath: "plan.md", kind: DocKind.Plan);
        await _index.IndexAsync(doc1, [CreateSection()], []);
        await _index.IndexAsync(doc2, [CreateSection()], []);

        await _index.UpsertRelationAsync(CreateRelation(sourcePath: "spec.md", targetPath: "plan.md"));
        await _index.DeleteRelationAsync(doc1.Id.Value, doc2.Id.Value, RelationKind.Implements);

        var relations = await _index.GetRelationsAsync(doc1.Id.Value, asSource: true);
        Assert.Empty(relations);
    }

    [Fact]
    public async Task GetRelationsAsync_AsTarget_ReturnsIncomingRelations()
    {
        var doc1 = CreateTestDoc(relPath: "spec.md");
        var doc2 = CreateTestDoc(relPath: "plan.md", kind: DocKind.Plan);
        await _index.IndexAsync(doc1, [CreateSection()], []);
        await _index.IndexAsync(doc2, [CreateSection()], []);

        await _index.UpsertRelationAsync(CreateRelation(sourcePath: "spec.md", targetPath: "plan.md"));

        var asSource = await _index.GetRelationsAsync(doc1.Id.Value, asSource: true);
        var asTarget = await _index.GetRelationsAsync(doc2.Id.Value, asSource: false);

        Assert.Single(asSource);
        Assert.Single(asTarget);
        Assert.Equal(doc2.Id.Value, asSource[0].TargetId.Value);
        Assert.Equal(doc1.Id.Value, asTarget[0].SourceId.Value);
    }

    [Fact]
    public async Task WriteAuditAsync_RecordsEntry()
    {
        var doc = CreateTestDoc();
        await _index.IndexAsync(doc, [CreateSection()], []);

        var entry = new DocAuditEntry(
            Ulid.NewUlid().ToString(),
            doc.Id.Value,
            "mcp:doc_save",
            "create",
            null,
            "{\"title\":\"Test\"}",
            DateTimeOffset.UtcNow);

        await _index.WriteAuditAsync(entry);

        var audit = await _index.GetAuditAsync(doc.Id.Value);
        Assert.Single(audit);
        Assert.Equal("mcp:doc_save", audit[0].Actor);
        Assert.Equal("create", audit[0].Action);
    }

    [Fact]
    public async Task GetAuditAsync_WithoutDocId_ReturnsAll()
    {
        var doc1 = CreateTestDoc(relPath: "a.md");
        var doc2 = CreateTestDoc(relPath: "b.md");
        await _index.IndexAsync(doc1, [CreateSection()], []);
        await _index.IndexAsync(doc2, [CreateSection()], []);

        await _index.WriteAuditAsync(new DocAuditEntry(
            Ulid.NewUlid().ToString(), doc1.Id.Value, "actor", "action", null, null, DateTimeOffset.UtcNow));
        await _index.WriteAuditAsync(new DocAuditEntry(
            Ulid.NewUlid().ToString(), doc2.Id.Value, "actor", "action", null, null, DateTimeOffset.UtcNow));

        var allAudit = await _index.GetAuditAsync();
        Assert.Equal(2, allAudit.Count);
    }

    [Fact]
    public async Task GetAuditAsync_WithLimit_RespectsLimit()
    {
        var doc = CreateTestDoc();
        await _index.IndexAsync(doc, [CreateSection()], []);

        for (int i = 0; i < 5; i++)
        {
            await _index.WriteAuditAsync(new DocAuditEntry(
                Ulid.NewUlid().ToString(), doc.Id.Value, "actor", $"action{i}", null, null, DateTimeOffset.UtcNow));
        }

        var audit = await _index.GetAuditAsync(doc.Id.Value, limit: 3);
        Assert.Equal(3, audit.Count);
    }

    [Fact]
    public async Task SetPolicyAsync_GetPolicyAsync_RoundTrips()
    {
        var policy = new DocPolicy("coverage", "{\"include\":[\"docs/**\"]}", DateTimeOffset.UtcNow);
        await _index.SetPolicyAsync(policy);

        var retrieved = await _index.GetPolicyAsync("coverage");
        Assert.NotNull(retrieved);
        Assert.Equal("coverage", retrieved!.Key);
        Assert.Equal("{\"include\":[\"docs/**\"]}", retrieved.Value);
    }

    [Fact]
    public async Task GetPolicyAsync_UnknownKey_ReturnsNull()
    {
        var policy = await _index.GetPolicyAsync("nonexistent");
        Assert.Null(policy);
    }

    [Fact]
    public async Task SetOfferStateAsync_GetOfferStateAsync_RoundTrips()
    {
        var state = new DocOfferState(
            "project-hash-123",
            OfferState.Accepted,
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow,
            3);

        await _index.SetOfferStateAsync(state);

        var retrieved = await _index.GetOfferStateAsync("project-hash-123");
        Assert.NotNull(retrieved);
        Assert.Equal(OfferState.Accepted, retrieved!.State);
        Assert.Equal(3, retrieved.SpecsSinceAsk);
    }

    [Fact]
    public async Task GetOfferStateAsync_UnknownProject_ReturnsNull()
    {
        var state = await _index.GetOfferStateAsync("unknown-project");
        Assert.Null(state);
    }

    [Fact]
    public async Task IndexAsync_DocWithFrontMatterError_PersistsError()
    {
        var doc = CreateTestDoc();
        // Create doc with frontmatter error using internal constructor
        var errorDoc = new Doc(
            doc.Id, doc.Ownership, doc.Origin, doc.RelPath, doc.Kind, doc.Title,
            doc.Status, doc.Topic, doc.Order, doc.SupersededBy, doc.StepsTotal, doc.StepsDone,
            doc.FileHash, doc.MTime,
            frontMatterError: true,
            frontMatterErrorMessage: "Invalid YAML");

        await _index.IndexAsync(errorDoc, [CreateSection()], []);

        var retrieved = await _index.GetAsync(doc.Id.Value);
        Assert.NotNull(retrieved);
        Assert.True(retrieved!.FrontMatterError);
        Assert.Equal("Invalid YAML", retrieved.FrontMatterErrorMessage);
    }

    [Fact]
    public async Task IndexAsync_PlanKind_TracksSteps()
    {
        var plan = CreateTestDoc(kind: DocKind.Plan, title: "Test Plan", stepsTotal: 10, stepsDone: 4);
        await _index.IndexAsync(plan, [CreateSection()], []);

        var retrieved = await _index.GetAsync(plan.Id.Value);
        Assert.NotNull(retrieved);
        Assert.Equal(10, retrieved!.StepsTotal);
        Assert.Equal(4, retrieved.StepsDone);
        Assert.Equal(0.4, retrieved.Progress, 2);
    }

    [Fact]
    public async Task IndexAsync_SupersededBy_TracksReplacement()
    {
        var doc = CreateTestDoc(
            supersededBy: "docs/specs/new-spec.md",
            status: DocStatusValue.FromSpec(SpecStatus.Superseded));
        await _index.IndexAsync(doc, [CreateSection()], []);

        var retrieved = await _index.GetAsync(doc.Id.Value);
        Assert.NotNull(retrieved);
        Assert.Equal("docs/specs/new-spec.md", retrieved!.SupersededBy);
        Assert.True(retrieved.Status.IsSuperseded());
    }

    [Fact]
    public async Task FTS_SelfHeal_RebuildsWhenEmpty()
    {
        // This tests the self-heal logic in EnsureSchemaAsync
        var doc = CreateTestDoc();
        await _index.IndexAsync(doc, [CreateSection(heading: "Test Section")], []);

        // Directly clear FTS tables to simulate corruption
        var connectionString = typeof(SqliteDocsIndex)
            .GetField("_connectionString", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(_index) as string;
        
        await using var connection = new SqliteConnection(connectionString!);
        await connection.OpenAsync();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM docs_fts_porter; DELETE FROM docs_fts_trigram;";
            await cmd.ExecuteNonQueryAsync();
        }
        await connection.CloseAsync();

        // Create new index instance pointing to same DB - should trigger self-heal
        var newIndex = new SqliteDocsIndex(_dbPath);
        var results = await newIndex.SearchAsync("test");
        
        // Should have healed and found the doc
        Assert.NotEmpty(results);
    }
}