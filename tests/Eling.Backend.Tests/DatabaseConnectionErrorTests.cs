using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eling.Backend.Endpoints;
using Eling.Backend.Mcp.Tools;
using Eling.Core.Audit;
using Eling.Core.Database;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eling.Backend.Tests;

/// <summary>
/// What a caller sees when a database call fails.
///
/// <para>
/// The dashboard gets a problem response, never a crash: a stored connection
/// that cannot be read is a broken registration, not a reason to answer 500.
/// </para>
/// <para>
/// The agent gets a category, never the provider's own words. A MySQL failure
/// reads "Access denied for user 'root'@'10.0.0.5'" - it names the account -
/// and the agent's boundary (the DB connection design spec, section 4.2)
/// forbids a credential in an error message as firmly as it forbids one in a
/// result.
/// </para>
/// </summary>
public sealed class DatabaseConnectionErrorTests : IAsyncLifetime
{
    /// <summary>A string MySQL's own builder refuses - it is not a connection string.</summary>
    private const string UnreadableConnectionString = "this is not a connection string";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "eling-db-errors-" + Guid.NewGuid().ToString("N"));

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
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        _store.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Query_WhenTheStoredStringIsUnreadable_AnswersAProblemInsteadOfCrashing()
    {
        var id = await RegisterUnreadableConnection();

        HttpResponseMessage? response = null;
        var thrown = await Record.ExceptionAsync(async () =>
        {
            response = await _client.PostAsync(
                $"/api/databases/{id}/query",
                Json("""{"sql":"SELECT 1","database":"orders"}"""));
        });

        Assert.Null(thrown);
        Assert.Equal(HttpStatusCode.BadGateway, response!.StatusCode);
    }

    [Fact]
    public async Task Mutate_WhenTheStoredStringIsUnreadable_AnswersAProblemInsteadOfCrashing()
    {
        var id = await RegisterUnreadableConnection();

        HttpResponseMessage? response = null;
        var thrown = await Record.ExceptionAsync(async () =>
        {
            response = await _client.PostAsync(
                $"/api/databases/{id}/mutate",
                Json("""{"sql":"DELETE FROM orders","database":"orders"}"""));
        });

        Assert.Null(thrown);
        Assert.Equal(HttpStatusCode.BadGateway, response!.StatusCode);
    }

    [Fact]
    public async Task AgentGetSchema_WhenTheProviderFails_AnswersWithTheCategoryAlone()
    {
        // A directory where a SQLite file is expected: opening it fails, and
        // the provider's own message says what it could not open.
        var notADatabase = Path.Combine(_root, "a-directory");
        Directory.CreateDirectory(notADatabase);

        var connection = DatabaseConnection.Create(
            "broken@local",
            DatabaseProvider.Sqlite,
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = notADatabase }.ToString(),
            DatabaseEnvironment.ReadOnly);
        await _store.UpsertAsync(connection, CancellationToken.None);

        var tool = new DatabaseGetSchemaTool(_store, NullLogger<DatabaseGetSchemaTool>.Instance);

        var thrown = await Record.ExceptionAsync(() => tool.GetSchemaAsync(connection.Id));

        var error = Assert.IsType<InvalidOperationException>(thrown);
        Assert.Equal("schema failed", error.Message);
    }

    [Fact]
    public async Task AgentQuery_WhenTheStoredStringIsUnreadable_AnswersWithTheCategoryAlone()
    {
        var connection = DatabaseConnection.Create(
            "broken@local",
            DatabaseProvider.MySql,
            UnreadableConnectionString,
            DatabaseEnvironment.ReadOnly);
        await _store.UpsertAsync(connection, CancellationToken.None);

        var tool = new DatabaseQueryTool(_store, logger: NullLogger<DatabaseQueryTool>.Instance);

        var thrown = await Record.ExceptionAsync(() => tool.QueryAsync(connection.Id, "SELECT 1", 100, "orders"));

        var error = Assert.IsType<InvalidOperationException>(thrown);
        Assert.Equal("query failed", error.Message);
    }

    /// <summary>
    /// Registers a connection whose stored string the provider cannot read, and
    /// returns its id.
    /// </summary>
    private async Task<string> RegisterUnreadableConnection()
    {
        var created = await _client.PostAsync("/api/databases", Json($$"""
            {
              "name": "broken@local",
              "provider": "MySql",
              "environment": "Development",
              "allowAgentMutations": true,
              "requireApprovalForMutations": false,
              "connectionString": "{{UnreadableConnectionString}}"
            }
            """));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        return JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString()!;
    }

    private static StringContent Json(string body) =>
        new(body, Encoding.UTF8, "application/json");
}
