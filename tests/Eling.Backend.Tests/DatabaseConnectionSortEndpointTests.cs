using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eling.Backend.Endpoints;
using Eling.Core.Audit;
using Eling.Core.Database;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Tests;

/// <summary>
/// The order the connection list is served in.
///
/// <para>
/// The server owns the order: a client asks for a column and a direction and
/// the rows come back already arranged. What these tests pin is that the
/// request is honoured, that an unknown column or direction falls back to the
/// default instead of refusing the request, and that the body itself did not
/// change shape while the order did.
/// </para>
/// </summary>
public sealed class DatabaseConnectionSortEndpointTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "eling-db-sort-" + Guid.NewGuid().ToString("N"));

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
        var scanner = new AppSettingsConnectionScanner(_root);
        builder.Services.AddSingleton(scanner);
        builder.Services.AddSingleton(new Eling.Backend.Database.DatabaseConnectionImporter(scanner, _store));
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });
        builder.WebHost.UseTestServer();
        _app = builder.Build();
        _app.MapDatabaseEndpoints();
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

    private async Task<string> Register(
        string name,
        DatabaseProvider provider,
        DateTimeOffset createdAt)
    {
        var connection = DatabaseConnection.Create(
            name,
            provider,
            "Server=localhost;Database=db;Uid=root;Pwd=pw",
            DatabaseEnvironment.ReadOnly,
            createdAt: createdAt);
        await _store.UpsertAsync(connection);
        return connection.Id;
    }

    private async Task MarkConnected(string id, DateTimeOffset at) =>
        await _store.MarkConnectedAsync(id, at);

    private async Task<List<string>> NamesAsync(string query)
    {
        var response = await _client.GetAsync("/api/databases" + query);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        return document.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()!)
            .ToList();
    }

    [Fact]
    public async Task List_WithoutParameters_AnswersTheDefaultNameOrder()
    {
        await Register("zebra", DatabaseProvider.MySql, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await Register("alpha", DatabaseProvider.MySql, new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));

        var names = await NamesAsync(string.Empty);

        Assert.Equal(new List<string> { "alpha", "zebra" }, names);
    }

    [Fact]
    public async Task List_WithSortNameAndAscending_AnswersTheNameOrder()
    {
        await Register("zebra", DatabaseProvider.MySql, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await Register("alpha", DatabaseProvider.MySql, new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));

        var names = await NamesAsync("?sort=name&order=asc");

        Assert.Equal(new List<string> { "alpha", "zebra" }, names);
    }

    [Fact]
    public async Task List_WithSortCreated_AnswersTheNewestFirst()
    {
        await Register("older", DatabaseProvider.MySql, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await Register("newer", DatabaseProvider.MySql, new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));

        var names = await NamesAsync("?sort=created");

        Assert.Equal(new List<string> { "newer", "older" }, names);
    }

    [Fact]
    public async Task List_WithSortLastUsed_AnswersTheMostRecentFirst()
    {
        var older = await Register("older", DatabaseProvider.MySql, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var newer = await Register("newer", DatabaseProvider.MySql, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await MarkConnected(older, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await MarkConnected(newer, new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));

        var names = await NamesAsync("?sort=lastUsed");

        Assert.Equal(new List<string> { "newer", "older" }, names);
    }

    [Fact]
    public async Task List_WithAnUnknownSort_FallsBackToTheDefault()
    {
        await Register("zebra", DatabaseProvider.MySql, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await Register("alpha", DatabaseProvider.MySql, new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));

        var response = await _client.GetAsync("/api/databases?sort=bogus");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var names = await NamesAsync("?sort=bogus");
        Assert.Equal(new List<string> { "alpha", "zebra" }, names);
    }

    [Fact]
    public async Task List_WithAnUnknownOrder_FallsBackToTheDefault()
    {
        await Register("zebra", DatabaseProvider.MySql, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await Register("alpha", DatabaseProvider.MySql, new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));

        var response = await _client.GetAsync("/api/databases?order=sideways");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var names = await NamesAsync("?order=sideways");
        Assert.Equal(new List<string> { "alpha", "zebra" }, names);
    }

    [Fact]
    public async Task List_WithABlankSort_FallsBackToTheDefault()
    {
        await Register("zebra", DatabaseProvider.MySql, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await Register("alpha", DatabaseProvider.MySql, new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));

        var response = await _client.GetAsync("/api/databases?sort=&order=");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var names = await NamesAsync("?sort=&order=");
        Assert.Equal(new List<string> { "alpha", "zebra" }, names);
    }

    [Fact]
    public async Task List_WithoutParameters_KeepsTheExistingBodyShape()
    {
        await Register("alpha", DatabaseProvider.MySql, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var response = await _client.GetAsync("/api/databases");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Array, root.ValueKind);

        var first = root.EnumerateArray().First();
        Assert.Equal("alpha", first.GetProperty("name").GetString());
        Assert.Equal("MySql", first.GetProperty("provider").GetString());
        Assert.True(first.TryGetProperty("createdAt", out _));
        Assert.True(first.TryGetProperty("lastConnectedAt", out _));
    }
}
