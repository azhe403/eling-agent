using System.Net;
using System.Net.Http.Json;
using Eling.Backend.Controllers;
using Eling.Backend.Updates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Eling.Backend.Tests.Updates;

/// <summary>
/// The status endpoint over a real TestServer with a stubbed checker: it must
/// serve the cached shape and never reach the network itself.
/// </summary>
public sealed class UpdateControllerTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var status = new UpdateStatus
        {
            CurrentVersion = "0.1.0-pre.30",
            LatestVersion = "v0.1.0-pre.32",
            UpdateAvailable = true,
            ReleaseUrl = "https://example.test/pre32",
            ReleaseNotes = "notes",
            CheckedAt = new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero),
            Channel = "prerelease",
        };

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IUpdateChecker>(new StubChecker(status));
        // Controllers are discovered from the entry assembly, which under a
        // test host is the test runner — the backend assembly has to be added
        // explicitly or the route 404s. Mirrors DashboardServices.
        builder.Services
            .AddControllers()
            .AddApplicationPart(typeof(UpdateController).Assembly);
        _app = builder.Build();
        _app.MapControllers();
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task GetStatus_ServesCachedShape()
    {
        var response = await _client.GetAsync("/api/update/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<UpdateStatusPayload>();
        Assert.NotNull(dto);
        Assert.True(dto.UpdateAvailable);
        Assert.Equal("v0.1.0-pre.32", dto.LatestVersion);
        Assert.Equal("https://example.test/pre32", dto.ReleaseUrl);
        Assert.Equal("prerelease", dto.Channel);
    }

    private sealed class StubChecker : IUpdateChecker
    {
        private readonly UpdateStatus _status;

        public StubChecker(UpdateStatus status)
        {
            _status = status;
        }

        public Task<UpdateStatus> GetStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_status);

        public Task<UpdateStatus> CheckNowAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_status);
    }

    private sealed class UpdateStatusPayload
    {
        public string CurrentVersion { get; set; } = string.Empty;
        public string? LatestVersion { get; set; }
        public bool UpdateAvailable { get; set; }
        public string? ReleaseUrl { get; set; }
        public string? ReleaseNotes { get; set; }
        public DateTimeOffset CheckedAt { get; set; }
        public string Channel { get; set; } = string.Empty;
    }
}
