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

namespace Eling.Backend.Tests;

/// <summary>
/// The dashboard registers a connection by typing host, port, database and
/// credentials instead of pasting a connection string. The backend builds the
/// string, so the credential is quoted by the provider's own builder and the
/// answer to the dashboard never carries it.
/// </summary>
public sealed class DatabaseManualConnectionTests : IAsyncLifetime
{
    private const string Username = "dashboard_agent_user";
    private const string Password = "mcp-secret-value";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "eling-db-manual-" + Guid.NewGuid().ToString("N"));

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private JsonDatabaseConnectionStore _store = null!;
    private string _registryPath = null!;
    private string _sqliteFile = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _registryPath = Path.Combine(_root, "databases.json");
        _sqliteFile = Path.Combine(_root, "manual.db");
        _store = new JsonDatabaseConnectionStore(_registryPath);

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
    public async Task Create_WithManualFields_StoresWhatWasTypedAndAnswersWithoutThePassword()
    {
        var res = await _client.PostAsync("/api/databases", Json($$"""
            {
              "name": "orders@10.0.0.5",
              "provider": "MySql",
              "environment": "ReadOnly",
              "allowAgentMutations": false,
              "requireApprovalForMutations": true,
              "manual": {
                "host": "10.0.0.5",
                "port": 3307,
                "database": "orders",
                "username": "{{Username}}",
                "password": "{{Password}}"
              }
            }
            """));

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Password, body);

        using var doc = JsonDocument.Parse(body);
        var details = doc.RootElement.GetProperty("details");
        Assert.Equal("10.0.0.5", details.GetProperty("host").GetString());
        Assert.Equal(3307, details.GetProperty("port").GetInt32());
        Assert.Equal("orders", details.GetProperty("database").GetString());
        Assert.Equal(Username, details.GetProperty("username").GetString());
        Assert.False(details.TryGetProperty("password", out _));

        var stored = Assert.Single(await _store.ListAsync(CancellationToken.None));
        var fields = DatabaseConnectionStringFactory.Parse(stored.Provider, stored.ConnectionString);
        Assert.NotNull(fields);
        Assert.Equal("10.0.0.5", fields.Host);
        Assert.Equal(3307, fields.Port);
        Assert.Equal("orders", fields.Database);
        Assert.Equal(Username, fields.Username);
        Assert.Equal(Password, fields.Password);
    }

    [Fact]
    public async Task Create_WithManualFieldsAndNoHost_IsRejected()
    {
        var res = await _client.PostAsync("/api/databases", Json("""
            {
              "name": "orders",
              "provider": "MySql",
              "environment": "ReadOnly",
              "allowAgentMutations": false,
              "requireApprovalForMutations": true,
              "manual": { "database": "orders" }
            }
            """));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Create_WithNoDatabase_StoresAServerLevelConnection()
    {
        var res = await _client.PostAsync("/api/databases", Json($$"""
            {
              "name": "10.0.0.5",
              "provider": "MySql",
              "environment": "ReadOnly",
              "allowAgentMutations": false,
              "requireApprovalForMutations": true,
              "manual": {
                "host": "10.0.0.5",
                "port": 3306,
                "username": "{{Username}}",
                "password": "{{Password}}"
              }
            }
            """));

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("isServerLevel").GetBoolean());
        var details = doc.RootElement.GetProperty("details");
        Assert.Equal("10.0.0.5", details.GetProperty("host").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, details.GetProperty("database").ValueKind);
        Assert.False(details.TryGetProperty("password", out _));

        var stored = Assert.Single(await _store.ListAsync(CancellationToken.None));
        var fields = DatabaseConnectionStringFactory.Parse(stored.Provider, stored.ConnectionString);
        Assert.NotNull(fields);
        Assert.Null(fields.Database);
        Assert.Equal(Password, fields.Password);
    }

    [Fact]
    public async Task TestConnection_WithManualSqliteFile_ReportsOk()
    {
        var res = await _client.PostAsync("/api/databases/test-connection", Json($$"""
            {
              "name": "manual@local",
              "provider": "Sqlite",
              "environment": "ReadOnly",
              "allowAgentMutations": false,
              "requireApprovalForMutations": true,
              "manual": { "filePath": "{{_sqliteFile.Replace("\\", "\\\\")}}" }
            }
            """));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Update_WithManualFieldsAndABlankPassword_KeepsTheStoredPassword()
    {
        var created = await _client.PostAsync("/api/databases", Json($$"""
            {
              "name": "orders@10.0.0.5",
              "provider": "MySql",
              "environment": "ReadOnly",
              "allowAgentMutations": false,
              "requireApprovalForMutations": true,
              "manual": {
                "host": "10.0.0.5",
                "port": 3306,
                "database": "orders",
                "username": "{{Username}}",
                "password": "{{Password}}"
              }
            }
            """));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString()!;

        var res = await _client.PutAsync($"/api/databases/{id}", Json("""
            {
              "name": "orders@10.0.0.5",
              "environment": "ReadOnly",
              "manual": { "host": "10.0.0.5", "port": 3307, "database": "orders" }
            }
            """));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var stored = Assert.Single(await _store.ListAsync(CancellationToken.None));
        var fields = DatabaseConnectionStringFactory.Parse(stored.Provider, stored.ConnectionString);
        Assert.NotNull(fields);
        Assert.Equal(3307, fields.Port);
        Assert.Equal(Password, fields.Password);
        Assert.Equal(Username, fields.Username);
    }

    [Fact]
    public async Task Update_WithABlankDatabase_ClearsTheStoredDatabase()
    {
        var created = await _client.PostAsync("/api/databases", Json($$"""
            {
              "name": "orders@10.0.0.5",
              "provider": "MySql",
              "environment": "ReadOnly",
              "allowAgentMutations": false,
              "requireApprovalForMutations": true,
              "manual": {
                "host": "10.0.0.5",
                "port": 3306,
                "database": "orders",
                "username": "{{Username}}",
                "password": "{{Password}}"
              }
            }
            """));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString()!;

        var res = await _client.PutAsync($"/api/databases/{id}", Json("""
            {
              "name": "10.0.0.5",
              "environment": "ReadOnly",
              "manual": { "host": "10.0.0.5", "port": 3306 }
            }
            """));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var stored = Assert.Single(await _store.ListAsync(CancellationToken.None));
        var fields = DatabaseConnectionStringFactory.Parse(stored.Provider, stored.ConnectionString);
        Assert.NotNull(fields);
        Assert.Null(fields.Database);
        // The password survived the edit even though the box was empty.
        Assert.Equal(Password, fields.Password);
    }

    [Fact]
    public async Task TestStoredConnection_WithoutABody_TestsTheStoredConnection()
    {
        var created = await _client.PostAsync("/api/databases", Json($$"""
            {
              "name": "manual@local",
              "provider": "Sqlite",
              "environment": "ReadOnly",
              "allowAgentMutations": false,
              "requireApprovalForMutations": true,
              "manual": { "filePath": "{{_sqliteFile.Replace("\\", "\\\\")}}" }
            }
            """));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString()!;

        var res = await _client.PostAsync($"/api/databases/{id}/test", content: null);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task TestStoredConnection_WithAManualBody_TestsTheEditedConnection()
    {
        var created = await _client.PostAsync("/api/databases", Json($$"""
            {
              "name": "manual@local",
              "provider": "Sqlite",
              "environment": "ReadOnly",
              "allowAgentMutations": false,
              "requireApprovalForMutations": true,
              "manual": { "filePath": "{{_sqliteFile.Replace("\\", "\\\\")}}" }
            }
            """));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString()!;

        var other = Path.Combine(_root, "other.db");
        var res = await _client.PostAsync($"/api/databases/{id}/test", Json($$"""
            {
              "manual": { "filePath": "{{other.Replace("\\", "\\\\")}}" }
            }
            """));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        // The test ran against the edited connection, so the other file is the
        // one that was opened - and the stored connection is untouched.
        Assert.True(File.Exists(other));
        var stored = Assert.Single(await _store.ListAsync(CancellationToken.None));
        var fields = DatabaseConnectionStringFactory.Parse(stored.Provider, stored.ConnectionString);
        Assert.NotNull(fields);
        Assert.Equal(_sqliteFile, fields.FilePath);
    }

    [Fact]
    public async Task ListDatabases_WithManualFields_ReachesTheServer()
    {
        // SQLite has no server holding several databases, so the answer is an
        // empty list - the endpoint itself works.
        var res = await _client.PostAsync("/api/databases/list-databases", Json($$"""
            {
              "name": "manual@local",
              "provider": "Sqlite",
              "environment": "ReadOnly",
              "allowAgentMutations": false,
              "requireApprovalForMutations": true,
              "manual": { "filePath": "{{_sqliteFile.Replace("\\", "\\\\")}}" }
            }
            """));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var data = await res.Content.ReadAsStringAsync();
        Assert.Equal("[]", data);
    }

    [Fact]
    public async Task ListDatabases_ForAMissingConnection_IsNotFound()
    {
        var res = await _client.PostAsync("/api/databases/does-not-exist/databases", Json("{}"));

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task ListDatabases_ForAStoredConnection_ReachesTheServer()
    {
        var created = await _client.PostAsync("/api/databases", Json($$"""
            {
              "name": "manual@local",
              "provider": "Sqlite",
              "environment": "ReadOnly",
              "allowAgentMutations": false,
              "requireApprovalForMutations": true,
              "manual": { "filePath": "{{_sqliteFile.Replace("\\", "\\\\")}}" }
            }
            """));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString()!;

        var res = await _client.PostAsync($"/api/databases/{id}/databases", Json("{}"));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("[]", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task McpList_NeverCarriesTheUsernameOrTheDetails()
    {
        await _client.PostAsync("/api/databases", Json($$"""
            {
              "name": "orders@10.0.0.5",
              "provider": "MySql",
              "environment": "ReadOnly",
              "allowAgentMutations": false,
              "requireApprovalForMutations": true,
              "manual": {
                "host": "10.0.0.5",
                "port": 3307,
                "database": "orders",
                "username": "{{Username}}",
                "password": "{{Password}}"
              }
            }
            """));

        var tool = new DatabaseListConnectionsTool(_store);
        var overview = await tool.ListAsync();

        var registered = Assert.Single(overview.Registered);
        Assert.Null(registered.Details);
        var json = JsonSerializer.Serialize(overview);
        Assert.DoesNotContain(Username, json);
        Assert.DoesNotContain(Password, json);
        Assert.DoesNotContain("Password=", json);
    }

    private static StringContent Json(string body) =>
        new(body, Encoding.UTF8, "application/json");
}
