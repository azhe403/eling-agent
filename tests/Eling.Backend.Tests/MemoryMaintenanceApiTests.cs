using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Eling.Backend.Bootstrap;
using Eling.Core;
using Xunit;

namespace Eling.Backend.Tests;

public class MemoryMaintenanceApiTests : IAsyncLifetime, IDisposable
{
    private readonly string _tempDir;
    private Microsoft.AspNetCore.Builder.WebApplication? _app;
    private HttpClient? _client;

    public MemoryMaintenanceApiTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "eling-maintenance-" + Guid.NewGuid().ToString("N")[..8]);
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, ".eling"));

        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        _app = TestAppBuilder.CreateSelfContained(dashboardPort: port);
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    }

    public async Task DisposeAsync()
    {
        if (_client is not null)
        {
            _client.Dispose();
            _client = null;
        }

        if (_app is not null)
        {
            try { await _app.StopAsync(); } catch { }
            try { await _app.DisposeAsync(); } catch { }
            _app = null;
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task PostMaintenance_DryRunDefault_ReturnsOkReport()
    {
        var response = await _client!.PostAsJsonAsync("/api/memory/maintenance", new MaintenanceRequest
        {
            Scope = MaintenanceScope.Project,
            DryRun = true
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<MaintenanceReport>(new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        Assert.NotNull(report);
        Assert.True(report.DryRun);
    }

    [Fact]
    public async Task PostMaintenance_InvalidThreshold_ReturnsBadRequest()
    {
        var response = await _client!.PostAsJsonAsync("/api/memory/maintenance", new MaintenanceRequest
        {
            SimilarityThreshold = 1.5
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
