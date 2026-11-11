using System.Net;
using System.Text;
using System.Text.Json;
using Eling.Backend.Endpoints;
using Eling.Core.Audit;
using Eling.Core.Database;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eling.Backend.Tests;

/// <summary>
/// The endpoint that lists the logical databases of a Redis connection.
///
/// <para>
/// The tree in the dashboard draws one row per database, so this answer is
/// what the tree is built from. Two things must hold: a connection that is
/// not Redis is refused with a message that says which kind of connection it
/// is, and a Redis connection answers with the databases and their key
/// counts.
/// </para>
/// </summary>
public sealed class RedisDatabaseListEndpointTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "eling-redis-dbs-" + Guid.NewGuid().ToString("N"));

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private JsonDatabaseConnectionStore _store = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _store = new JsonDatabaseConnectionStore(Path.Combine(_root, "databases.json"));

        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddLogging();
        builder.Services.AddSingleton<IAuditLogger>(NullAuditLogger.Instance);
        builder.Services.AddSingleton<IDatabaseConnectionStore>(_store);
        builder.Services.AddSingleton<IRedisKeyReadService>(_ => new StubRedisKeyReadService(
        [
            new RedisDatabaseInfo(3, 3),
            new RedisDatabaseInfo(5, 128),
        ]));
        // The command route drops the cached inspection of its operand, so the
        // cache is a handler parameter even when the stub never fills it.
        builder.Services.AddSingleton<IRedisReadCache>(_ => new JsonRedisReadCache(_root));
        builder.WebHost.UseTestServer();
        _app = builder.Build();
        _app.MapDatabaseKeyEndpoints();
        await _app.StartAsync();
        _client = _app.CreateEnvelopeClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        _store.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task ListDatabases_ForAnUnknownId_AnswersNotFound()
    {
        var response = await _client.GetAsync("/api/databases/nope/keys/databases");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ListDatabases_ForANonRedisConnection_AnswersBadRequest()
    {
        var id = await RegisterConnection("local.sqlite", DatabaseProvider.Sqlite, "Data Source=local.db");

        var response = await _client.GetAsync($"/api/databases/{id}/keys/databases");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("key browsing requires Redis", body);
    }

    [Fact]
    public async Task ListDatabases_ForARedisConnection_AnswersTheDatabaseList()
    {
        var id = await RegisterConnection("dev-ibl-redis", DatabaseProvider.Redis, "dev-ibl-redis.internal:6379");

        var response = await _client.GetAsync($"/api/databases/{id}/keys/databases");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var rows = document.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(3, rows[0].GetProperty("index").GetInt32());
        Assert.Equal(3, rows[0].GetProperty("keys").GetInt64());
        Assert.Equal(5, rows[1].GetProperty("index").GetInt32());
        Assert.Equal(128, rows[1].GetProperty("keys").GetInt64());
    }

    private async Task<string> RegisterConnection(string name, DatabaseProvider provider, string connectionString)
    {
        // Straight into the store: this test is about the key endpoints, and
        // the connection list endpoint is a different subject.
        var connection = DatabaseConnection.Create(
            name,
            provider,
            connectionString,
            DatabaseEnvironment.Development);
        await _store.UpsertAsync(connection, CancellationToken.None);
        return connection.Id;
    }

    /// <summary>
    /// The database list without a Redis server: the endpoint's own answer is
    /// what is under test, not the connection to one.
    /// </summary>
    private sealed class StubRedisKeyReadService(IReadOnlyList<RedisDatabaseInfo> databases) : IRedisKeyReadService
    {
        public Task<IReadOnlyList<RedisDatabaseInfo>> ListDatabasesAsync(
            DatabaseConnection connection, CancellationToken cancellationToken = default, bool preferCache = true)
            => Task.FromResult(databases);

        public Task<KeyPage> ScanAsync(
            DatabaseConnection connection,
            RedisDatabaseIndex database,
            string prefix,
            long cursor,
            int pageOffset,
            int pageSize,
            CancellationToken cancellationToken = default,
            bool preferCache = true)
            => throw new NotSupportedException("scan is not part of this test.");

        public Task<KeyInspectResult> InspectAsync(
            DatabaseConnection connection,
            RedisDatabaseIndex database,
            string key,
            CancellationToken cancellationToken = default, bool preferCache = true)
            => throw new NotSupportedException("inspect is not part of this test.");

        public Task<RedisKeyProbeResult> ProbeKeyAsync(
            DatabaseConnection connection,
            RedisDatabaseIndex database,
            string key,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("probe is not part of this test.");

        public Task<IReadOnlyList<RedisKeyProbeResult>> ProbeKeysAsync(
            DatabaseConnection connection,
            RedisDatabaseIndex database,
            IReadOnlyList<string> keys,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("probe is not part of this test.");

        public Task<RedisCommandResult> ExecuteCommandAsync(
            DatabaseConnection connection,
            RedisDatabaseIndex database,
            string command,
            IReadOnlyList<string> args,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("command is not part of this test.");

        public Task<RedisKeyDeleteResult> DeleteKeyAsync(
            DatabaseConnection connection,
            RedisDatabaseIndex database,
            string key,
            CancellationToken ct = default)
            => throw new NotSupportedException("delete is not part of this test.");

        public Task<RedisPrefixPreviewResult> PreviewPrefixAsync(
            DatabaseConnection connection,
            RedisDatabaseIndex database,
            string prefix,
            CancellationToken ct = default)
            => throw new NotSupportedException("preview is not part of this test.");

        public Task<RedisPrefixDeleteResult> DeletePrefixAsync(
            DatabaseConnection connection,
            RedisDatabaseIndex database,
            string prefix,
            CancellationToken ct = default)
            => throw new NotSupportedException("prefix delete is not part of this test.");
    }
}
