using System.Text.Json;
using Eling.Backend.Database;
using Eling.Backend.Mcp.Tools;
using Eling.Core.Database;

namespace Eling.Backend.Tests;

public sealed class DatabaseDetectImportTests : IDisposable
{
    private const string BaseConnection =
        "Server=localhost;Database=orders;User ID=app;Password=base-secret;";
    private const string StagingConnection =
        "Server=staging.internal;Database=orders;User ID=app;Password=staging-secret;";

    private readonly string _dir;
    private readonly string _registryPath;

    public DatabaseDetectImportTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"eling-db-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _registryPath = Path.Combine(_dir, "databases.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory must not fail the test run.
        }
    }

    [Fact]
    public async Task ImportAsync_QualifiedHandle_ImportsTheEnvironmentFileValue()
    {
        // Arrange - the same key in the base file and in the environment file.
        Write("appsettings.json", ConnectionStrings("Default", BaseConnection));
        Write("appsettings.Staging.json", ConnectionStrings("Default", StagingConnection));
        var store = NewStore();
        var importer = new DatabaseConnectionImporter(NewScanner(), store);

        // Act
        var results = await importer.ImportAsync(["Staging:Default"]);

        // Assert - the staging value, not the base one.
        Assert.Single(results);
        Assert.True(results[0].Imported);
        Assert.Equal("orders@staging.internal", results[0].Name);

        var registered = await store.ListAsync();
        Assert.Single(registered);
        Assert.Contains("staging.internal", registered[0].ConnectionString);
        Assert.DoesNotContain("localhost", registered[0].ConnectionString);
    }

    [Fact]
    public async Task ImportAsync_BaseHandle_ImportsTheBaseFileValue()
    {
        // Arrange
        Write("appsettings.json", ConnectionStrings("Default", BaseConnection));
        Write("appsettings.Staging.json", ConnectionStrings("Default", StagingConnection));
        var store = NewStore();
        var importer = new DatabaseConnectionImporter(NewScanner(), store);

        // Act
        var results = await importer.ImportAsync(["Default"]);

        // Assert
        Assert.True(results[0].Imported);
        var registered = await store.ListAsync();
        Assert.Single(registered);
        Assert.Contains("localhost", registered[0].ConnectionString);
    }

    [Fact]
    public async Task ImportAsync_HandleIsCaseInsensitive()
    {
        // Arrange
        Write("appsettings.json", ConnectionStrings("Default", BaseConnection));
        Write("appsettings.Staging.json", ConnectionStrings("Default", StagingConnection));
        var store = NewStore();
        var importer = new DatabaseConnectionImporter(NewScanner(), store);

        // Act - configuration keys are case-insensitive, so a user may type the
        // handle in any casing.
        var results = await importer.ImportAsync(["staging:default"]);

        // Assert
        Assert.True(results[0].Imported);
        Assert.Equal("orders@staging.internal", results[0].Name);
    }

    [Fact]
    public async Task ImportAsync_UnknownHandle_ReportsNotFound()
    {
        // Arrange
        Write("appsettings.json", ConnectionStrings("Default", BaseConnection));
        var store = NewStore();
        var importer = new DatabaseConnectionImporter(NewScanner(), store);

        // Act
        var results = await importer.ImportAsync(["Production:Default"]);

        // Assert
        Assert.Single(results);
        Assert.False(results[0].Imported);
        Assert.Equal("not found in source", results[0].Reason);
        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task ImportAsync_SameNameTwice_ReportsAlreadyExists()
    {
        // Arrange - both files point at the same database, so the second import
        // would overwrite the first.
        Write("appsettings.json", ConnectionStrings("Default", BaseConnection));
        Write("appsettings.Staging.json", ConnectionStrings("Same", BaseConnection));
        var store = NewStore();
        var importer = new DatabaseConnectionImporter(NewScanner(), store);

        // Act
        await importer.ImportAsync(["Default"]);
        var results = await importer.ImportAsync(["Staging:Same"]);

        // Assert
        Assert.False(results[0].Imported);
        Assert.Equal("already exists", results[0].Reason);
        Assert.Single(await store.ListAsync());
    }

    [Fact]
    public async Task ImportAsync_RegistersAsReadOnly()
    {
        // Arrange
        Write("appsettings.json", ConnectionStrings("Default", BaseConnection));
        var store = NewStore();
        var importer = new DatabaseConnectionImporter(NewScanner(), store);

        // Act
        await importer.ImportAsync(["Default"]);

        // Assert
        var registered = Assert.Single(await store.ListAsync());
        Assert.Equal(DatabaseEnvironment.ReadOnly, registered.Environment);
        Assert.False(registered.AllowAgentMutations);
        Assert.True(registered.RequireApprovalForMutations);
    }

    [Fact]
    public async Task DetectTool_Output_CarriesNoConnectionString()
    {
        // Arrange
        Write("appsettings.json", ConnectionStrings("Default", BaseConnection));
        Write("appsettings.Staging.json", ConnectionStrings("Default", StagingConnection));
        var tool = new DatabaseDetectConnectionsTool(NewScanner());

        // Act
        var candidates = await tool.DetectAsync();

        // Assert - identity only: the agent never sees a credential.
        Assert.Equal(2, candidates.Count);
        var json = JsonSerializer.Serialize(candidates);
        Assert.DoesNotContain("base-secret", json);
        Assert.DoesNotContain("staging-secret", json);
        Assert.DoesNotContain("Password=", json);
        Assert.Contains("Staging:Default", json);
        Assert.Contains("appsettings.Staging.json", json);
    }

    [Fact]
    public async Task ListTool_ReportsTheEnvironmentCandidateAsUnimported()
    {
        // Arrange - the case the fix is about: the environment file's
        // connection has to reach the unimported list.
        Write("appsettings.json", ConnectionStrings("Default", BaseConnection));
        Write("appsettings.Staging.json", ConnectionStrings("Default", StagingConnection));
        var tool = new DatabaseListConnectionsTool(NewStore(), NewScanner());

        // Act
        var overview = await tool.ListAsync();

        // Assert
        Assert.Empty(overview.Registered);
        Assert.Equal(2, overview.Unimported.Count);
        Assert.Contains(overview.Unimported, c => c.SettingsKey == "Staging:Default");
        Assert.Contains(overview.Unimported, c => c.SourceFile == "appsettings.Staging.json");
    }

    private AppSettingsConnectionScanner NewScanner() => new(_dir);

    private JsonDatabaseConnectionStore NewStore() => new(_registryPath);

    private void Write(string fileName, string content)
        => File.WriteAllText(Path.Combine(_dir, fileName), content);

    private static string ConnectionStrings(string key, string connection)
        => $$$"""{"ConnectionStrings": {"{{{key}}}": "{{{connection}}}"}}""";
}
