using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Eling.Backend.Endpoints;
using Eling.Core.Audit;
using Eling.Core.Database;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace Eling.Backend.Tests;

/// <summary>
/// The delete routes that hang off <c>/api/databases/{id}/keys</c>: delete one
/// key, preview a prefix subtree, and delete the whole subtree, each gated the
/// same way the read routes are (unknown id 404, non-Redis 400, out-of-range
/// database 400), with the read service's own failures mapped by the shared
/// run ladder (missing key 404, credentials 502, unavailable 503).
/// </summary>
public sealed class RedisKeyDeleteEndpointTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "eling-redis-delete-" + Guid.NewGuid().ToString("N"));

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private JsonDatabaseConnectionStore _store = null!;
    private StubRedisKeyReadService _stub = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _store = new JsonDatabaseConnectionStore(Path.Combine(_root, "databases.json"));

        _stub = new StubRedisKeyReadService();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddLogging();
        builder.Services.AddSingleton<IAuditLogger>(NullAuditLogger.Instance);
        builder.Services.AddSingleton<IDatabaseConnectionStore>(_store);
        builder.Services.AddSingleton<IRedisKeyReadService>(_stub);
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
    public async Task DeleteKey_UnknownId_NotFound()
    {
        var response = await _client.DeleteAsync("/api/databases/nope/keys/delete?key=user:1:email");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteKey_NonRedis_BadRequestContainsKeyBrowsingRequiresRedis()
    {
        var id = await RegisterConnection("local.sqlite", DatabaseProvider.Sqlite, "Data Source=local.db");

        var response = await _client.DeleteAsync($"/api/databases/{id}/keys/delete?key=user:1:email");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("key browsing requires Redis", body);
    }

    [Fact]
    public async Task DeleteKey_OutOfRangeDatabase_BadRequestRefusesIndex()
    {
        var id = await RegisterConnection("dev-ibl-redis", DatabaseProvider.Redis, "dev-ibl-redis.internal:6379");

        var response = await _client.DeleteAsync($"/api/databases/{id}/keys/delete?database=16&key=user:1:email");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("database must be an integer between 0 and 15", body);
    }

    [Fact]
    public async Task DeleteKey_BlankKey_BadRequest()
    {
        var id = await RegisterConnection("dev-ibl-redis", DatabaseProvider.Redis, "dev-ibl-redis.internal:6379");

        var response = await _client.DeleteAsync($"/api/databases/{id}/keys/delete?key=");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DeleteKey_Redis_AnswersDeletedTrue()
    {
        var id = await RegisterConnection("dev-ibl-redis", DatabaseProvider.Redis, "dev-ibl-redis.internal:6379");

        var response = await _client.DeleteAsync($"/api/databases/{id}/keys/delete?key=user:1:email");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(document.RootElement.GetProperty("deleted").GetBoolean());
    }

    [Fact]
    public async Task ProbeKey_UnknownId_NotFound()
    {
        var response = await _client.GetAsync("/api/databases/nope/keys/probe?key=user:1:email");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ProbeKey_NonRedis_BadRequestContainsKeyBrowsingRequiresRedis()
    {
        var id = await RegisterConnection("local.sqlite", DatabaseProvider.Sqlite, "Data Source=local.db");

        var response = await _client.GetAsync($"/api/databases/{id}/keys/probe?key=user:1:email");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("key browsing requires Redis", body);
    }

    [Fact]
    public async Task ProbeKey_BlankKey_BadRequest()
    {
        var id = await RegisterConnection("dev-ibl-redis", DatabaseProvider.Redis, "dev-ibl-redis.internal:6379");

        var response = await _client.GetAsync($"/api/databases/{id}/keys/probe?key=");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ProbeKey_OutOfRangeDatabase_BadRequestRefusesIndex()
    {
        var id = await RegisterConnection("dev-ibl-redis", DatabaseProvider.Redis, "dev-ibl-redis.internal:6379");

        var response = await _client.GetAsync($"/api/databases/{id}/keys/probe?database=16&key=user:1:email");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("database must be an integer between 0 and 15", body);
    }

    [Fact]
    public async Task ProbeKey_ExistingKey_AnswersLiveExpiry()
    {
        _stub.ProbeBehavior = key => Task.FromResult(new RedisKeyProbeResult(
            key, true, 120, DateTimeOffset.UtcNow.AddSeconds(120)));
        var id = await RegisterConnection("dev-ibl-redis", DatabaseProvider.Redis, "dev-ibl-redis.internal:6379");

        var response = await _client.GetAsync($"/api/databases/{id}/keys/probe?key=user:1:email");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("user:1:email", root.GetProperty("key").GetString());
        Assert.True(root.GetProperty("exists").GetBoolean());
        Assert.Equal(120, root.GetProperty("ttlSeconds").GetInt64());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("expiresAt").GetString()));
    }

    [Fact]
    public async Task ProbeKey_GoneKey_AnswersExistsFalseRatherThan404()
    {
        // A recheck is a question about a row already on screen; "it is gone" is
        // an answer, not a failure, so it is a 200 with exists:false.
        _stub.ProbeBehavior = key => Task.FromResult(new RedisKeyProbeResult(key, false, -1, null));
        var id = await RegisterConnection("dev-ibl-redis", DatabaseProvider.Redis, "dev-ibl-redis.internal:6379");

        var response = await _client.GetAsync($"/api/databases/{id}/keys/probe?key=user:1:email");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(document.RootElement.GetProperty("exists").GetBoolean());
    }

    [Fact]
    public async Task ProbeKeys_EmptyList_BadRequest()
    {
        var id = await RegisterConnection("dev-ibl-redis", DatabaseProvider.Redis, "dev-ibl-redis.internal:6379");

        var response = await _client.PostAsJsonAsync($"/api/databases/{id}/keys/probe", new { database = 0, keys = Array.Empty<string>() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ProbeKeys_BlankKeysOnly_BadRequest()
    {
        var id = await RegisterConnection("dev-ibl-redis", DatabaseProvider.Redis, "dev-ibl-redis.internal:6379");

        var response = await _client.PostAsJsonAsync($"/api/databases/{id}/keys/probe", new { database = 0, keys = new[] { "", "" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ProbeKeys_NonRedis_BadRequest()
    {
        var id = await RegisterConnection("local.sqlite", DatabaseProvider.Sqlite, "Data Source=local.db");

        var response = await _client.PostAsJsonAsync($"/api/databases/{id}/keys/probe", new { database = 0, keys = new[] { "a" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ProbeKeys_OutOfRangeDatabase_BadRequestRefusesIndex()
    {
        var id = await RegisterConnection("dev-ibl-redis", DatabaseProvider.Redis, "dev-ibl-redis.internal:6379");

        var response = await _client.PostAsJsonAsync($"/api/databases/{id}/keys/probe", new { database = 16, keys = new[] { "a" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("database must be an integer between 0 and 15", body);
    }

    [Fact]
    public async Task ProbeKeys_AnswersEachKeyOnce()
    {
        _stub.ProbeManyBehavior = keys => Task.FromResult<IReadOnlyList<RedisKeyProbeResult>>(
            keys.Distinct(StringComparer.Ordinal)
                .Select(k => new RedisKeyProbeResult(k, k != "gone:1", k == "gone:1" ? -1L : 60, null))
                .ToList());
        var id = await RegisterConnection("dev-ibl-redis", DatabaseProvider.Redis, "dev-ibl-redis.internal:6379");

        // The same key named twice is one key: it must not come back twice.
        var response = await _client.PostAsJsonAsync($"/api/databases/{id}/keys/probe", new
        {
            database = 3,
            keys = new[] { "alive:1", "gone:1", "alive:1" },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var rows = document.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);
        var alive = rows.Single(r => r.GetProperty("key").GetString() == "alive:1");
        Assert.True(alive.GetProperty("exists").GetBoolean());
        var gone = rows.Single(r => r.GetProperty("key").GetString() == "gone:1");
        Assert.False(gone.GetProperty("exists").GetBoolean());
    }

    [Fact]
    public async Task ProbeKeys_TooManyKeys_BadRequest()
    {
        var id = await RegisterConnection("dev-ibl-redis", DatabaseProvider.Redis, "dev-ibl-redis.internal:6379");

        var response = await _client.PostAsJsonAsync($"/api/databases/{id}/keys/probe", new
        {
            database = 0,
            keys = Enumerable.Range(0, 501).Select(i => $"k{i}").ToArray(),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("at most 500 keys per probe", body);
    }

    [Fact]
    public async Task PreviewPrefix_EmptyPrefix_BadRequest()
    {
        var id = await RegisterConnection("dev-ibl-redis", DatabaseProvider.Redis, "dev-ibl-redis.internal:6379");

        var response = await _client.GetAsync($"/api/databases/{id}/keys/preview?prefix=");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PreviewPrefix_Redis_AnswersCappedList()
    {
        var id = await RegisterConnection("dev-ibl-redis", DatabaseProvider.Redis, "dev-ibl-redis.internal:6379");

        var response = await _client.GetAsync($"/api/databases/{id}/keys/preview?prefix=user%3A");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("user:", document.RootElement.GetProperty("prefix").GetString());
        Assert.Equal(100, document.RootElement.GetProperty("keys").GetArrayLength());
        Assert.Equal(150, document.RootElement.GetProperty("total").GetInt64());
        Assert.True(document.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task DeletePrefix_EmptyPrefix_BadRequest()
    {
        var id = await RegisterConnection("dev-ibl-redis", DatabaseProvider.Redis, "dev-ibl-redis.internal:6379");

        var response = await _client.DeleteAsync($"/api/databases/{id}/keys/prefix?prefix=");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DeletePrefix_Redis_AnswersCount()
    {
        var id = await RegisterConnection("dev-ibl-redis", DatabaseProvider.Redis, "dev-ibl-redis.internal:6379");

        var response = await _client.DeleteAsync($"/api/databases/{id}/keys/prefix?prefix=user%3A");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, document.RootElement.GetProperty("deleted").GetInt64());
        Assert.False(document.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task DeleteKey_MissingKey_NotFoundBody()
    {
        var id = await RegisterConnection("dev-ibl-redis", DatabaseProvider.Redis, "dev-ibl-redis.internal:6379");
        _stub.DeleteKeyBehavior = _ => Task.FromException<RedisKeyDeleteResult>(
            new KeyNotFoundException("key not found"));

        var response = await _client.DeleteAsync($"/api/databases/{id}/keys/delete?key=user:1:email");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("key not found", body);
    }

    [Fact]
    public async Task DeleteKey_Unreachable_AnswersUnavailable()
    {
        var id = await RegisterConnection("dev-ibl-redis", DatabaseProvider.Redis, "dev-ibl-redis.internal:6379");
        _stub.DeleteKeyBehavior = _ => Task.FromException<RedisKeyDeleteResult>(
            new TimeoutException("Connection timeout expired."));

        var response = await _client.DeleteAsync($"/api/databases/{id}/keys/delete?key=user:1:email");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("redis unavailable", body);
    }

    [Fact]
    public async Task DeleteKey_CredentialsRejected_AnswersCredentialsRejected()
    {
        var id = await RegisterConnection("dev-ibl-redis", DatabaseProvider.Redis, "dev-ibl-redis.internal:6379");
        _stub.DeleteKeyBehavior = _ => Task.FromException<RedisKeyDeleteResult>(
            new RedisConnectionException(ConnectionFailureType.AuthenticationFailure, "WRONGPASS invalid username-password pair"));

        var response = await _client.DeleteAsync($"/api/databases/{id}/keys/delete?key=user:1:email");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("redis credentials rejected", body);
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
    /// The key service without a Redis server: the endpoints' own answers are
    /// what is under test, not the connection to one. The delete behaviors are
    /// set per test so the error ladder can be exercised one rung at a time.
    /// </summary>
    private sealed class StubRedisKeyReadService : IRedisKeyReadService
    {
        public Func<string, Task<RedisKeyDeleteResult>> DeleteKeyBehavior { get; set; } =
            _ => Task.FromResult(new RedisKeyDeleteResult(true));

        public Func<string, Task<RedisPrefixPreviewResult>> PreviewBehavior { get; set; } =
            prefix => Task.FromResult(new RedisPrefixPreviewResult(
                prefix,
                Enumerable.Range(1, 100).Select(i => prefix + i).ToList(),
                150,
                true));

        public Func<string, Task<RedisPrefixDeleteResult>> PrefixDeleteBehavior { get; set; } =
            _ => Task.FromResult(new RedisPrefixDeleteResult(2, false));

        public Func<string, Task<RedisKeyProbeResult>> ProbeBehavior { get; set; } =
            key => Task.FromResult(new RedisKeyProbeResult(key, false, -1L, null));

        public Func<IReadOnlyList<string>, Task<IReadOnlyList<RedisKeyProbeResult>>> ProbeManyBehavior { get; set; } =
            keys => Task.FromResult<IReadOnlyList<RedisKeyProbeResult>>(
                keys.Select(k => new RedisKeyProbeResult(k, false, -1L, null)).ToList());

        public Task<RedisKeyDeleteResult> DeleteKeyAsync(
            DatabaseConnection connection,
            RedisDatabaseIndex database,
            string key,
            CancellationToken ct = default)
            => DeleteKeyBehavior(key);

        public Task<RedisPrefixPreviewResult> PreviewPrefixAsync(
            DatabaseConnection connection,
            RedisDatabaseIndex database,
            string prefix,
            CancellationToken ct = default)
            => PreviewBehavior(prefix);

        public Task<RedisPrefixDeleteResult> DeletePrefixAsync(
            DatabaseConnection connection,
            RedisDatabaseIndex database,
            string prefix,
            CancellationToken ct = default)
            => PrefixDeleteBehavior(prefix);

        public Task<IReadOnlyList<RedisDatabaseInfo>> ListDatabasesAsync(
            DatabaseConnection connection,
            CancellationToken cancellationToken = default,
            bool preferCache = true)
            => throw new NotSupportedException("databases are not part of this test.");

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

        public Task<RedisKeyProbeResult> ProbeKeyAsync(
            DatabaseConnection connection,
            RedisDatabaseIndex database,
            string key,
            CancellationToken cancellationToken = default)
            => ProbeBehavior(key);

        public Task<IReadOnlyList<RedisKeyProbeResult>> ProbeKeysAsync(
            DatabaseConnection connection,
            RedisDatabaseIndex database,
            IReadOnlyList<string> keys,
            CancellationToken cancellationToken = default)
            => ProbeManyBehavior(keys);

        public Task<KeyInspectResult> InspectAsync(
            DatabaseConnection connection,
            RedisDatabaseIndex database,
            string key,
            CancellationToken cancellationToken = default, bool preferCache = true)
            => throw new NotSupportedException("inspect is not part of this test.");

        public Task<RedisCommandResult> ExecuteCommandAsync(
            DatabaseConnection connection,
            RedisDatabaseIndex database,
            string command,
            IReadOnlyList<string> args,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("command is not part of this test.");
    }
}
