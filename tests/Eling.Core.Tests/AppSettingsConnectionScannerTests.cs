using Eling.Core.Database;

namespace Eling.Core.Tests;

public sealed class AppSettingsConnectionScannerTests : IDisposable
{
    private const string BaseConnection =
        "Server=localhost;Database=orders;User ID=app;Password=base-secret;";
    private const string StagingConnection =
        "Server=staging.internal;Database=orders;User ID=app;Password=staging-secret;";

    private readonly string _dir;

    public AppSettingsConnectionScannerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"eling-appsettings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
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
    public void Scan_OnDirectory_ReadsEveryAppSettingsFile()
    {
        // Arrange
        Write("appsettings.json", ConnectionStrings("Default", BaseConnection));
        Write("appsettings.Staging.json", ConnectionStrings("StagingDb", StagingConnection));

        // Act
        var detected = new AppSettingsConnectionScanner(_dir).Scan();

        // Assert
        Assert.Equal(2, detected.Count);
        Assert.Contains(detected, d =>
            d.SourceFile == "appsettings.json" && d.SettingsKey == "Default");
        Assert.Contains(detected, d =>
            d.SourceFile == "appsettings.Staging.json" && d.SettingsKey == "Staging:StagingDb");
    }

    [Fact]
    public void Scan_OnDirectory_SameKeyInTwoFiles_KeepsBothCandidates()
    {
        // Arrange - the same key in the base file and in the environment file.
        Write("appsettings.json", ConnectionStrings("Default", BaseConnection));
        Write("appsettings.Staging.json", ConnectionStrings("Default", StagingConnection));

        // Act
        var detected = new AppSettingsConnectionScanner(_dir).Scan();

        // Assert - two candidates, not one: the base key and the qualified one.
        Assert.Equal(2, detected.Count);
        Assert.Contains(detected, d => d.SettingsKey == "Default");
        Assert.Contains(detected, d => d.SettingsKey == "Staging:Default");
        Assert.Equal(
            ["Default", "Staging:Default"],
            detected.Select(d => d.SettingsKey).ToArray());
    }

    [Fact]
    public void Scan_OnDirectory_ListsTheBaseFileFirst()
    {
        // Arrange
        Write("appsettings.json", ConnectionStrings("Default", BaseConnection));
        Write("appsettings.Staging.json", ConnectionStrings("StagingDb", StagingConnection));
        Write("appsettings.Development.json", ConnectionStrings("DevDb", BaseConnection));

        // Act
        var detected = new AppSettingsConnectionScanner(_dir).Scan();

        // Assert
        Assert.Equal("appsettings.json", detected[0].SourceFile);
        Assert.Equal(
            new string?[] { "appsettings.Development.json", "appsettings.Staging.json" },
            detected.Skip(1).Select(d => d.SourceFile).ToArray());
    }

    [Fact]
    public void Scan_OnDirectory_QualifiesTheHandleWithTheEnvironmentName()
    {
        // Arrange - a lower-case environment name keeps its own casing, so two
        // files that differ only in case stay two handles.
        Write("appsettings.staging.json", ConnectionStrings("Default", StagingConnection));

        // Act
        var detected = new AppSettingsConnectionScanner(_dir).Scan();

        // Assert
        Assert.Equal("staging:Default", detected[0].SettingsKey);
        Assert.Equal("appsettings.staging.json", detected[0].SourceFile);
    }

    [Fact]
    public void Scan_OnDirectory_HandleThatCollides_FallsBackToTheFileName()
    {
        // Arrange - a base key that already looks like a qualified handle.
        Write("appsettings.json", ConnectionStrings("Staging:Default", BaseConnection));
        Write("appsettings.Staging.json", ConnectionStrings("Default", StagingConnection));

        // Act
        var detected = new AppSettingsConnectionScanner(_dir).Scan();

        // Assert
        Assert.Equal("Staging:Default", detected[0].SettingsKey);
        Assert.Equal("appsettings.Staging.json:Default", detected[1].SettingsKey);
    }

    [Fact]
    public void Scan_OnSingleFile_ReadsOnlyThatFile()
    {
        // Arrange
        var file = Path.Combine(_dir, "appsettings.json");
        File.WriteAllText(file, ConnectionStrings("Default", BaseConnection));
        Write("appsettings.Staging.json", ConnectionStrings("StagingDb", StagingConnection));

        // Act
        var detected = new AppSettingsConnectionScanner(file).Scan();

        // Assert
        Assert.Single(detected);
        Assert.Equal("Default", detected[0].SettingsKey);
    }

    [Fact]
    public void Scan_MasksThePassword_AndNeverReturnsIt()
    {
        // Arrange
        Write("appsettings.json", ConnectionStrings("Default", BaseConnection));

        // Act
        var detected = new AppSettingsConnectionScanner(_dir).Scan();

        // Assert
        Assert.Contains("Password=***", detected[0].MaskedConnectionString);
        Assert.DoesNotContain("base-secret", detected[0].MaskedConnectionString);
    }

    [Fact]
    public void Scan_NamesTheConnectionFromItsDatabaseAndHost()
    {
        // Arrange
        Write("appsettings.json", ConnectionStrings("Default", BaseConnection));

        // Act
        var detected = new AppSettingsConnectionScanner(_dir).Scan();

        // Assert
        Assert.Equal("orders@localhost", detected[0].Name);
        Assert.Equal(DatabaseProvider.MySql, detected[0].Provider);
    }

    [Fact]
    public void Scan_OnCorruptFile_SkipsThatFileAndKeepsTheOthers()
    {
        // Arrange
        Write("appsettings.json", ConnectionStrings("Default", BaseConnection));
        Write("appsettings.Broken.json", "{ this is not json");

        // Act
        var detected = new AppSettingsConnectionScanner(_dir).Scan();

        // Assert
        Assert.Single(detected);
        Assert.Equal("Default", detected[0].SettingsKey);
    }

    [Fact]
    public void Scan_OnMissingDirectory_ReturnsNothing()
    {
        // Act
        var detected = new AppSettingsConnectionScanner(
            Path.Combine(_dir, "does-not-exist")).Scan();

        // Assert
        Assert.Empty(detected);
    }

    [Fact]
    public void Scan_FindsANestedConnectionString()
    {
        // Arrange
        Write("appsettings.json", """
            {
              "DatabaseSettings": {
                "ConnectionString": "Server=localhost;Database=orders;User ID=app;Password=base-secret;",
                "Version": "1"
              }
            }
            """);

        // Act
        var detected = new AppSettingsConnectionScanner(_dir).Scan();

        // Assert
        Assert.Single(detected);
        Assert.Equal("DatabaseSettings", detected[0].SettingsKey);
        Assert.Equal("orders@localhost", detected[0].Name);
    }

    [Fact]
    public void Scan_ReportsAnUnrecognisedValue_WithoutAPassword()
    {
        // Arrange - neither builder claims this value, so it stays visible but
        // not importable.
        Write("appsettings.json", ConnectionStrings("Broken", "Bogus=1"));

        // Act
        var detected = new AppSettingsConnectionScanner(_dir).Scan();

        // Assert
        Assert.Single(detected);
        Assert.Null(detected[0].Provider);
        Assert.Equal("unrecognized connection string", detected[0].RejectionReason);
        Assert.Equal("***", detected[0].MaskedConnectionString);
    }

    private void Write(string fileName, string content)
        => File.WriteAllText(Path.Combine(_dir, fileName), content);

    private static string ConnectionStrings(string key, string connection)
        => $$$"""{"ConnectionStrings": {"{{{key}}}": "{{{connection}}}"}}""";
}
