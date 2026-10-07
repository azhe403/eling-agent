using Eling.Backend.Bootstrap;
using Eling.Core;
using Eling.Core.Scope;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eling.Backend.Tests;

[Collection(ElingDataDirCollection.Name)]
public sealed class HttpLoopConfigurationIsolationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalCwd;

    public HttpLoopConfigurationIsolationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "eling-cfg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _originalCwd = Environment.CurrentDirectory;
    }

    [Fact]
    public void BuildWebApplication_DoesNotLoadAppSettingsFromCurrentDirectory()
    {
        var appsettingsPath = Path.Combine(_tempDir, "appsettings.json");
        File.WriteAllText(appsettingsPath, """
        {
          "CustomWorkspaceKey": "leaked_value",
          "Kestrel": {
            "Endpoints": {
              "LeakedHttp": {
                "Url": "http://*:4099"
              }
            }
          }
        }
        """);

        Environment.CurrentDirectory = _tempDir;
        try
        {
            var chain = ScopeChain.Discover(_tempDir);
            var userScope = UserScope.Resolve(Path.Combine(_tempDir, "user_scope"));
            var context = new ProjectContext(chain, userScope, Path.Combine(_tempDir, ".eling"), IsUserHome: false);
            var shared = AppServices.Create(context);

            using var app = HttpLoop.BuildWebApplication(
                shared,
                context,
                dashboardPort: 4317,
                isDevMode: false,
                NullLoggerFactory.Instance);

            var config = app.Services.GetRequiredService<IConfiguration>();
            Assert.Null(config["CustomWorkspaceKey"]);
            Assert.Null(config["Kestrel:Endpoints:LeakedHttp:Url"]);
        }
        finally
        {
            Environment.CurrentDirectory = _originalCwd;
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
        }
    }
}
