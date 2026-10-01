using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Eling.Backend.Bootstrap;
using Eling.Backend.Controllers;
using Eling.Backend.Dtos;
using Eling.Core.Codebase;
using Eling.Core.Memory;
using Eling.Core.Scope;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eling.Backend.Tests;

/// <summary>
/// The file viewer's two routes, over a real TestServer: the chunk lookup, the
/// disk read, and the path guard that has to hold for the second one.
/// </summary>
[Collection(ElingDataDirCollection.Name)]
public sealed class CodebaseFileControllerTests : IAsyncLifetime
{
    private const string DataDirEnv = "ELING_DATA_DIR";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private string _workspace = null!;
    private string _dataDir = null!;
    private string? _originalDataDir;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        _workspace = Path.Combine(Path.GetTempPath(), "eling-fileapi-ws-" + tag);
        _dataDir = Path.Combine(Path.GetTempPath(), "eling-fileapi-data-" + tag);
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(_dataDir);

        _originalDataDir = Environment.GetEnvironmentVariable(DataDirEnv);
        Environment.SetEnvironmentVariable(DataDirEnv, _dataDir);

        File.WriteAllText(Path.Combine(_workspace, "app.cs"), "using System;\n\nclass App { }\n");

        var index = new CodebaseIndexService(
            _workspace,
            new SqliteCodebaseIndex(ElingPaths.ResolveCodebaseDbPath(_workspace)));
        await index.IndexAsync(full: true);

        var registry = new RuntimeRegistry(
            NullLogger<RuntimeRegistry>.Instance,
            UserScope.Resolve(Path.Combine(_dataDir, "user-scope")));

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(index);
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton<CodebaseFileReader>();
        // Controllers are discovered from the entry assembly, which under a
        // test host is the test runner — the backend assembly has to be added
        // explicitly or the route 404s. Mirrors DashboardServices.
        builder.Services
            .AddControllers()
            .AddApplicationPart(typeof(CodebaseFileController).Assembly)
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            });

        var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        _client = app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        Environment.SetEnvironmentVariable(DataDirEnv, _originalDataDir);
        CodebaseRebuildScopeTests.TryDelete(_workspace);
        CodebaseRebuildScopeTests.TryDelete(_dataDir);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task GetChunks_ReturnsTheIndexedChunks()
    {
        var res = await _client.GetAsync($"/api/codebase/file?path=app.cs&project={Uri.EscapeDataString(_workspace)}");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<CodebaseFileDetailResponse>(Json);
        Assert.NotNull(body);
        Assert.Equal("app.cs", body!.Path);
        Assert.NotEmpty(body.Chunks);
        Assert.Contains("class App", body.Chunks[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetChunks_WithoutPath_ReturnsBadRequest()
    {
        var res = await _client.GetAsync($"/api/codebase/file?project={Uri.EscapeDataString(_workspace)}");

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task GetChunks_WithUnindexedPath_ReturnsNotFound()
    {
        var res = await _client.GetAsync($"/api/codebase/file?path=missing.cs&project={Uri.EscapeDataString(_workspace)}");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task GetContent_ReturnsTheFileText()
    {
        var res = await _client.GetAsync(
            $"/api/codebase/file/content?path=app.cs&project={Uri.EscapeDataString(_workspace)}");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<CodebaseFileContentResponse>(Json);
        Assert.NotNull(body);
        Assert.Equal("ok", body!.Status);
        Assert.Contains("class App", body.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetContent_WithoutProject_ReturnsBadRequest()
    {
        // A disk read has to name one workspace; there is no "all" for a file.
        var res = await _client.GetAsync("/api/codebase/file/content?path=app.cs");

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task GetContent_WithEscapingPath_ReturnsBadRequest()
    {
        // The traversal guard, proven end to end over HTTP rather than only on
        // the reader: "..%2f..%2f" is the shape an attacker would actually send.
        var res = await _client.GetAsync(
            $"/api/codebase/file/content?path=..%2F..%2FWindows%2Fwin.ini&project={Uri.EscapeDataString(_workspace)}");

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task GetContent_WithMissingFile_ReportsNotFoundStatus()
    {
        var res = await _client.GetAsync(
            $"/api/codebase/file/content?path=gone.cs&project={Uri.EscapeDataString(_workspace)}");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<CodebaseFileContentResponse>(Json);
        Assert.NotNull(body);
        Assert.Equal("notFound", body!.Status);
        Assert.Null(body.Content);
    }

    [Fact]
    public async Task GetContent_WithExcludedRoot_ReturnsBadRequest()
    {
        // ELING_CODEBASE_EXCLUDE is what keeps a temporal checkout out of the
        // index; it must keep it out of the disk read too.
        var excluded = Path.Combine(Path.GetTempPath(), "eling-fileapi-excluded-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(excluded);
        var previous = Environment.GetEnvironmentVariable("ELING_CODEBASE_EXCLUDE");
        try
        {
            Environment.SetEnvironmentVariable("ELING_CODEBASE_EXCLUDE", excluded);
            File.WriteAllText(Path.Combine(excluded, "secret.cs"), "secret");

            var res = await _client.GetAsync(
                $"/api/codebase/file/content?path=secret.cs&project={Uri.EscapeDataString(excluded)}");

            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ELING_CODEBASE_EXCLUDE", previous);
            CodebaseRebuildScopeTests.TryDelete(excluded);
        }
    }
}
