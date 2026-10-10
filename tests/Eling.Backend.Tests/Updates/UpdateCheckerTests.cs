using System.Net;
using Eling.Backend.Updates;
using Eling.Core.Scope;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Eling.Backend.Tests.Updates;

/// <summary>
/// The checker must earn its network calls: one fetch per stale period, cache
/// served otherwise, and failures degrading to the last known status instead of
/// ever throwing at the caller.
/// </summary>
public sealed class UpdateCheckerTests : IDisposable
{
    private const string ReleasesJson = """
        [
          {"tag_name": "v0.1.0-pre.99", "draft": true, "html_url": "https://example.test/draft", "body": "draft"},
          {"tag_name": "v0.1.0-pre.32", "draft": false, "html_url": "https://example.test/pre32", "body": "notes"}
        ]
        """;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"eling-update-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    [Fact]
    public async Task Cache_RoundTripsAcrossInstances_WithSnakeCaseWireNames()
    {
        Directory.CreateDirectory(_root);
        var status = new UpdateStatus
        {
            CurrentVersion = "0.1.0-pre.30",
            LatestVersion = "v0.1.0-pre.32",
            UpdateAvailable = true,
            ReleaseUrl = "https://example.test/pre32",
            CheckedAt = new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero),
            Channel = "prerelease",
        };

        var first = NewCache();
        first.SaveStatus(status);
        var reloaded = NewCache().GetStatus();

        Assert.NotNull(reloaded);
        Assert.Equal("v0.1.0-pre.32", reloaded.LatestVersion);
        Assert.True(reloaded.UpdateAvailable);
        Assert.Contains("\"update_available\"", File.ReadAllText(CachePath));
    }

    [Fact]
    public async Task CheckNow_SelectsNewestNonDraftRelease()
    {
        var checker = NewChecker("0.1.0-pre.30", out var handler);

        var status = await checker.CheckNowAsync();

        Assert.True(status.UpdateAvailable);
        Assert.Equal("v0.1.0-pre.32", status.LatestVersion);
        Assert.Equal("https://example.test/pre32", status.ReleaseUrl);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task CheckNow_BareBaseCurrent_CannotProveItIsBehind()
    {
        // Unstamped installs report the bare base 0.1.0, which no pre-release of
        // 0.1.0 can beat on precedence. The offer stays silent rather than lie.
        var checker = NewChecker("0.1.0", out _);

        var status = await checker.CheckNowAsync();

        Assert.False(status.UpdateAvailable);
    }

    [Fact]
    public async Task CheckNow_CurrentRelease_ReturnsNoUpdate()
    {
        var checker = NewChecker("0.1.0-pre.32+sha.abc1234", out _);

        var status = await checker.CheckNowAsync();

        Assert.False(status.UpdateAvailable);
    }

    [Fact]
    public async Task CheckNow_SecondCallWithinInterval_ServesCache()
    {
        var checker = NewChecker("0.1.0-pre.30", out var handler);

        await checker.CheckNowAsync();
        var cached = await checker.CheckNowAsync();

        Assert.Equal(1, handler.Calls);
        Assert.Equal("v0.1.0-pre.32", cached.LatestVersion);
    }

    [Fact]
    public async Task CheckNow_NetworkFailure_ReturnsUnknownWithoutThrowing()
    {
        var checker = NewChecker("0.1.0-pre.30", out _, failing: true);

        var status = await checker.CheckNowAsync();

        Assert.False(status.UpdateAvailable);
        Assert.Null(status.LatestVersion);
    }

    [Fact]
    public async Task CheckNow_WhenDisabled_SkipsNetworkAndReportsDisabled()
    {
        var previous = Environment.GetEnvironmentVariable("ELING_DISABLE_UPDATE_CHECK");
        Environment.SetEnvironmentVariable("ELING_DISABLE_UPDATE_CHECK", "1");
        try
        {
            var checker = NewChecker("0.1.0-pre.30", out var handler);

            var status = await checker.CheckNowAsync();

            Assert.False(status.UpdateAvailable);
            Assert.Equal("disabled", status.Channel);
            Assert.Equal(0, handler.Calls);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ELING_DISABLE_UPDATE_CHECK", previous);
        }
    }

    private UpdateChecker NewChecker(string currentVersion, out FakeHandler handler, bool failing = false)
    {
        Directory.CreateDirectory(_root);
        handler = new FakeHandler(ReleasesJson, failing);
        var client = new GitHubReleaseClient(
            new HttpClient(handler),
            NullLogger<GitHubReleaseClient>.Instance);
        return new UpdateChecker(
            client,
            NewCache(),
            NullLogger<UpdateChecker>.Instance,
            currentVersion);
    }

    private FileUpdateCache NewCache() => new(
        new UserScope(_root),
        NullLogger<FileUpdateCache>.Instance);

    private string CachePath => Path.Combine(_root, "config", "update-check.json");

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly string _json;
        private readonly bool _failing;

        public FakeHandler(string json, bool failing)
        {
            _json = json;
            _failing = failing;
        }

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            if (_failing)
            {
                throw new HttpRequestException("simulated network failure");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json),
            });
        }
    }
}
