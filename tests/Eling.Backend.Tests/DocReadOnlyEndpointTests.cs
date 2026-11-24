using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Eling.Backend.Bootstrap;
using Eling.Backend.Endpoints;
using Eling.Backend.Mcp.Tools;
using Eling.Core.Docs;
using Eling.Core.Docs.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Xunit;

namespace Eling.Backend.Tests;

/// <summary>
/// The docs-management spec requires external-target writes to answer
/// 409 doc_read_only (the same envelope code shape SkillEndpoints uses), and
/// defines a single doc_relation MCP tool. Both contracts are verified here.
/// </summary>
[Collection(ElingDataDirCollection.Name)]
public sealed class DocReadOnlyEndpointTests : IAsyncLifetime, IDisposable
{
    private WebApplication? _app;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        _app = TestAppBuilder.CreateSelfContained(dashboardPort: port);
        await _app.StartAsync();
        _client = new HttpClient(new EnvelopeTestHandler { InnerHandler = new HttpClientHandler() })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}")
        };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null)
        {
            try { await _app.StopAsync(); }
            catch (Exception ex) { Console.WriteLine($"StopAsync failed during teardown: {ex.Message}"); }
            try { await _app.DisposeAsync(); }
            catch (Exception ex) { Console.WriteLine($"DisposeAsync failed during teardown: {ex.Message}"); }
        }
    }

    public void Dispose() => GC.SuppressFinalize(this);

    private static Doc ExternalDoc(string relPath, DocKind kind)
        => new(
            DocId.Create(DocOwnership.External, DocOrigin.Current, relPath),
            DocOwnership.External,
            DocOrigin.Current,
            relPath,
            kind,
            "External doc",
            DocStatusValue.DefaultFor(kind),
            "specs",
            0,
            null,
            0,
            0,
            "hash",
            DateTimeOffset.UtcNow);

    [Fact]
    public async Task Annotate_ExternalDoc_Returns409WithDocReadOnlyCode()
    {
        var index = _app!.Services.GetRequiredKeyedService<IDocsIndex>("current");
        var doc = ExternalDoc("docs/specs/read-only.md", DocKind.Spec);
        await index.IndexAsync(doc, [], []);

        var res = await _client!.PostAsJsonAsync(
            $"/api/docs/{doc.Id.Value}/annotate",
            new { anchor = "section-1", status = 0, annotation = "note" });

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var failure = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("doc_read_only", failure.GetProperty("code").GetString());
    }

    private static Doc ManagedDoc(string relPath)
        => new(
            DocId.Create(DocOwnership.Managed, DocOrigin.Current, relPath),
            DocOwnership.Managed,
            DocOrigin.Current,
            relPath,
            DocKind.Spec,
            "Managed doc",
            DocStatusValue.DefaultFor(DocKind.Spec),
            "specs",
            0,
            null,
            0,
            0,
            "hash",
            DateTimeOffset.UtcNow);

    [Fact]
    public async Task DocRelationTool_WritesAndReadsThroughOneRegistration()
    {
        var index = _app!.Services.GetRequiredKeyedService<IDocsIndex>("current");
        var auditIndex = _app.Services.GetRequiredKeyedService<IDocsIndex>("shard");
        var source = ManagedDoc(".eling/docs/specs/source.md");
        var target = ManagedDoc(".eling/docs/specs/target.md");
        await index.IndexAsync(source, [], []);
        await index.IndexAsync(target, [], []);
        // Audit rows are always written to the shard index, whose doc_audit.doc_id
        // has an FK to docs(id); seed both endpoints there as production does.
        await auditIndex.IndexAsync(source, [], []);
        await auditIndex.IndexAsync(target, [], []);

        var tool = new DocsReadTool(index, _app.Services.GetRequiredService<Eling.Core.Docs.DocsService>());
        var relations = await tool.GetRelationsAsync(
            source.Id.Value,
            sourceId: source.Id.Value,
            targetId: target.Id.Value,
            kind: "references");

        Assert.Contains(relations, r => r.SourceId == source.Id.Value && r.TargetId == target.Id.Value);
    }

    [Fact]
    public void McpToolNames_InDocsAssembly_AreUnique()
    {
        var toolTypes = typeof(DocEndpoints).Assembly.GetTypes()
            .Where(t => t.GetCustomAttributes(typeof(McpServerToolTypeAttribute), inherit: false).Length > 0);

        var names = toolTypes
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .SelectMany(m => m.GetCustomAttributes(typeof(McpServerToolAttribute), inherit: false)
                .Cast<McpServerToolAttribute>()
                .Select(a => string.IsNullOrEmpty(a.Name) ? m.Name : a.Name))
            .ToList();

        Assert.NotEmpty(names);
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
