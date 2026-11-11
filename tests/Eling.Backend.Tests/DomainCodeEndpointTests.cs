using System.Net;
using System.Text.Json;
using Eling.Backend.Endpoints;
using Eling.Core.Audit;
using Eling.Core.Database;
using Eling.Core.Projects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Tests;

/// <summary>
/// The polished endpoints carry domain codes so the dashboard can react to a
/// specific failure, not just a status. The 404/403/415 paths
/// are what these tests pin, one per code.
/// </summary>
public sealed class DomainCodeEndpointTests : IAsyncLifetime
{
    private readonly string _root;
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public DomainCodeEndpointTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "eling-domaincode-" + Guid.NewGuid().ToString("N"));
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "hello.txt"), "hello");
        await File.WriteAllBytesAsync(Path.Combine(_root, "binary.bin"), [0x89, 0x50, 0x00, 0x47]);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddLogging();
        builder.Services.AddSingleton<IAuditLogger>(NullAuditLogger.Instance);
        builder.Services.AddSingleton<IDatabaseConnectionStore>(new JsonDatabaseConnectionStore(Path.Combine(_root, "databases.json")));
        builder.Services.AddSingleton<IDatabaseReadCache>(sp => new JsonDatabaseReadCache(
            _root,
            sp.GetRequiredService<ILogger<JsonDatabaseReadCache>>()));
        builder.Services.AddSingleton<IDatabaseReadService>(sp => new DatabaseReadService(
            sp.GetRequiredService<IDatabaseReadCache>(),
            sp.GetRequiredService<ILogger<DatabaseReadService>>()));
        builder.Services.AddSingleton<IRedisConnectionPool>(new RedisConnectionPool());
        builder.Services.AddSingleton<IRedisReadCache>(sp => new JsonRedisReadCache(
            _root,
            sp.GetRequiredService<ILogger<JsonRedisReadCache>>()));
        builder.Services.AddSingleton(new AppSettingsConnectionScanner(_root));
        builder.Services.AddSingleton(sp => new Eling.Backend.Database.DatabaseConnectionImporter(
            sp.GetRequiredService<AppSettingsConnectionScanner>(),
            sp.GetRequiredService<IDatabaseConnectionStore>()));
        builder.Services.AddSingleton<IWorkspacesRegistry>(new StubWorkspacesRegistry());
        // The redis read service is never reached by these tests (every key route
        // answers 404 for a missing connection before touching Redis), but it must
        // exist in the container or route mapping refuses the GET routes.
        builder.Services.AddSingleton<IRedisKeyReadService>(new StubRedisKeyReadService());
        builder.Services.AddSingleton(new Eling.Backend.Agent.Services.BackendFileTools(Microsoft.Extensions.Logging.Abstractions.NullLogger<Eling.Backend.Agent.Services.BackendFileTools>.Instance));
        builder.Services.ConfigureHttpJsonOptions(options => { });
        builder.WebHost.UseTestServer();

        _app = builder.Build();
        _app.UseMiddleware<Eling.Backend.Bootstrap.ApiEnvelopeMiddleware>();
        _app.MapDatabaseEndpoints();
        _app.MapDatabaseKeyEndpoints();
        _app.MapAgentWorkspaceEndpoints();
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static async Task<(HttpStatusCode Status, JsonDocument Doc)> JsonAsync(HttpResponseMessage res)
    {
        var raw = await res.Content.ReadAsStringAsync();
        return (res.StatusCode, JsonDocument.Parse(raw));
    }

    [Fact]
    public async Task MissingConnection_Schema_CodeIs_ConnectionNotFound()
    {
        var res = await _client.GetAsync("/api/databases/does-not-exist/schema");
        var (status, doc) = await JsonAsync(res);
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("connection_not_found", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("data").ValueKind);
        Assert.True(doc.RootElement.GetProperty("message").GetString()!.Length >= 6);
    }

    [Fact]
    public async Task MissingConnection_Keys_CodeIs_ConnectionNotFound()
    {
        var res = await _client.GetAsync("/api/databases/does-not-exist/keys");
        var (status, doc) = await JsonAsync(res);
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("connection_not_found", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task PathEscapingWorkspaceRoot_CodeIs_PathOutsideRoot()
    {
        var root = Uri.EscapeDataString(_root);
        var res = await _client.GetAsync($"/api/agent/files/list?root={root}&path=../outside");
        var (status, doc) = await JsonAsync(res);
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("path_outside_root", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task BinaryFile_CodeIs_BinaryFile()
    {
        var root = Uri.EscapeDataString(_root);
        var res = await _client.GetAsync($"/api/agent/files/read?root={root}&path=binary.bin");
        var (status, doc) = await JsonAsync(res);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, status);
        Assert.Equal("binary_file", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task TextFileList_SuccessCarriesVerbDefaultMessage()
    {
        var root = Uri.EscapeDataString(_root);
        var res = await _client.GetAsync($"/api/agent/files/list?root={root}");
        var (status, doc) = await JsonAsync(res);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("ok", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal("The resource was read successfully from the server.", doc.RootElement.GetProperty("message").GetString());
    }

    private sealed class StubWorkspacesRegistry : IWorkspacesRegistry
    {
        public void Record(RegisteredWorkspace workspace) { }
        public Task<IReadOnlyList<RegisteredWorkspace>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RegisteredWorkspace>>([]);
        public Task<RegisteredWorkspace?> FindByRootAsync(string root, CancellationToken cancellationToken = default)
            => Task.FromResult<RegisteredWorkspace?>(null);
        public Task<bool> DeleteByRootAsync(string root, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
        public Task<int> DeleteMissingRootsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(0);
    }

    private sealed class StubRedisKeyReadService : IRedisKeyReadService
    {
        private static NotSupportedException Unused() =>
            new("the Redis read service is never reached in these tests - every key route answers 404 first");

        public Task<KeyPage> ScanAsync(DatabaseConnection connection, RedisDatabaseIndex database, string prefix, long cursor, int pageOffset, int pageSize, CancellationToken cancellationToken = default, bool preferCache = true) => throw Unused();
        public Task<KeyInspectResult> InspectAsync(DatabaseConnection connection, RedisDatabaseIndex database, string key, CancellationToken cancellationToken = default) => throw Unused();
        public Task<RedisKeyProbeResult> ProbeKeyAsync(DatabaseConnection connection, RedisDatabaseIndex database, string key, CancellationToken cancellationToken = default) => throw Unused();
        public Task<IReadOnlyList<RedisKeyProbeResult>> ProbeKeysAsync(DatabaseConnection connection, RedisDatabaseIndex database, IReadOnlyList<string> keys, CancellationToken cancellationToken = default) => throw Unused();
        public Task<RedisCommandResult> ExecuteCommandAsync(DatabaseConnection connection, RedisDatabaseIndex database, string command, IReadOnlyList<string> args, CancellationToken cancellationToken = default) => throw Unused();
        public Task<IReadOnlyList<RedisDatabaseInfo>> ListDatabasesAsync(DatabaseConnection connection, CancellationToken cancellationToken = default, bool preferCache = true) => throw Unused();
        public Task<RedisKeyDeleteResult> DeleteKeyAsync(DatabaseConnection connection, RedisDatabaseIndex database, string key, CancellationToken ct = default) => throw Unused();
        public Task<RedisPrefixPreviewResult> PreviewPrefixAsync(DatabaseConnection connection, RedisDatabaseIndex database, string prefix, CancellationToken ct = default) => throw Unused();
        public Task<RedisPrefixDeleteResult> DeletePrefixAsync(DatabaseConnection connection, RedisDatabaseIndex database, string prefix, CancellationToken ct = default) => throw Unused();
    }
}
