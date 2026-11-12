using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Eling.Backend.Bootstrap;
using Eling.Core.Shell;
using Microsoft.AspNetCore.Builder;

namespace Eling.Backend.Tests.Endpoints;

[Collection(ElingDataDirCollection.Name)]
public sealed class ShellEndpointsTests : IAsyncLifetime, IDisposable
{
    private WebApplication? _app;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        _app = TestAppBuilder.CreateSelfContained(dashboardPort: port);
        await _app.StartAsync();
        _client = new HttpClient(new EnvelopeTestHandler { InnerHandler = new HttpClientHandler() }) { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    }

    [Fact]
    public async Task GetShellConfig_OnEmptyState_ReturnsDefaultTemplate()
    {
        var config = await GetConfigAsync(_client!);

        Assert.False(config.Enabled);
        Assert.True(config.RootGuardrail);
        Assert.True(config.RestrictToAllowedDirectories);
        Assert.Equal(120, config.DefaultTimeoutSeconds);
        Assert.Equal(65536, config.MaxOutputBytes);
        Assert.Empty(config.AllowedDirectories);
        Assert.Empty(config.BlockedCommands);
        Assert.NotEqual(default, config.UpdatedAt);
    }

    [Fact]
    public async Task PutShellConfig_ToggleEnabled_PersistsAndRoundTrips()
    {
        var client = _client!;
        var before = await GetConfigAsync(client);

        var put = await client.PutAsJsonAsync("/api/shell/config", before with { Enabled = true }, WebJson);
        put.EnsureSuccessStatusCode();
        var putBody = await put.Content.ReadFromJsonAsync<ShellConfig>(WebJson);

        Assert.NotNull(putBody);
        Assert.True(putBody!.Enabled);

        var after = await GetConfigAsync(client);
        Assert.True(after.Enabled);
        Assert.Equal(before.RestrictToAllowedDirectories, after.RestrictToAllowedDirectories);
        Assert.Equal(before.RootGuardrail, after.RootGuardrail);
        Assert.Equal(before.DefaultTimeoutSeconds, after.DefaultTimeoutSeconds);
        Assert.Equal(before.MaxOutputBytes, after.MaxOutputBytes);
        Assert.Empty(after.AllowedDirectories);
        Assert.Empty(after.BlockedCommands);
    }

    [Fact]
    public async Task PutShellConfig_OutOfRange_ReturnsBadRequest()
    {
        var client = _client!;
        var before = await GetConfigAsync(client);

        var badTimeout = await client.PutAsJsonAsync(
            "/api/shell/config",
            before with { DefaultTimeoutSeconds = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, badTimeout.StatusCode);

        var badOutput = await client.PutAsJsonAsync(
            "/api/shell/config",
            before with { MaxOutputBytes = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, badOutput.StatusCode);

        var unchanged = await GetConfigAsync(client);
        Assert.Equal(before.DefaultTimeoutSeconds, unchanged.DefaultTimeoutSeconds);
        Assert.Equal(before.MaxOutputBytes, unchanged.MaxOutputBytes);
    }

    [Fact]
    public async Task PutShellConfig_DropEmptyRules_SucceedsAndRoundTrips()
    {
        var client = _client!;

        var body = await PutConfigAsync(client, ConfigWithDirectories(
            BlankPathDirectory(""),
            WhitelistEntry("C:\\proj\\keep", enabled: true),
            BlankPathDirectory("   ")));

        var rule = Assert.Single(body.AllowedDirectories);
        Assert.Equal("C:\\proj\\keep", rule.Path);

        var roundTripped = await GetConfigAsync(client);
        var persisted = Assert.Single(roundTripped.AllowedDirectories);
        Assert.Equal("C:\\proj\\keep", persisted.Path);
    }

    [Fact]
    public async Task PutShellConfig_TogglesRuleEnabled_StampRuleUpdatedAt()
    {
        var client = _client!;
        var seeded = await PutConfigAsync(client, ConfigWithDirectories(
            WhitelistEntry("C:\\proj\\one", enabled: true),
            WhitelistEntry("C:\\proj\\two", enabled: true)));

        Assert.All(seeded.AllowedDirectories, rule => Assert.Null(rule.UpdatedAt));

        // 50ms clears the ~15.6ms Windows clock tick, so the top-level stamp
        // is guaranteed to differ between the two writes.
        await Task.Delay(50);

        var flipped = await PutConfigAsync(client, seeded with
        {
            AllowedDirectories = seeded.AllowedDirectories
                .Select(rule => rule.Path == "C:\\proj\\one" ? rule with { Enabled = false } : rule)
                .ToList(),
        });

        var one = flipped.AllowedDirectories.Single(rule => rule.Path == "C:\\proj\\one");
        var two = flipped.AllowedDirectories.Single(rule => rule.Path == "C:\\proj\\two");

        Assert.False(one.Enabled);
        Assert.NotNull(one.UpdatedAt);
        Assert.True(two.Enabled);
        Assert.Null(two.UpdatedAt);
        Assert.True(flipped.UpdatedAt > seeded.UpdatedAt);
    }

    [Fact]
    public async Task PutShellConfig_ReorderedRuleEnabledFlip_DoesNotMatchByIndex()
    {
        var client = _client!;

        // One rule starts off, so the enabled difference sits on a different
        // index once the body rotates the list: key matching stamps b, an
        // index-based delta stamps a instead.
        var seeded = await PutConfigAsync(client, ConfigWithDirectories(
            WhitelistEntry("C:\\proj\\a", enabled: true),
            WhitelistEntry("C:\\proj\\b", enabled: false)));

        var flipped = await PutConfigAsync(client, seeded with
        {
            AllowedDirectories =
            [
                seeded.AllowedDirectories.Single(rule => rule.Path == "C:\\proj\\b") with { Enabled = true },
                seeded.AllowedDirectories.Single(rule => rule.Path == "C:\\proj\\a") with { Enabled = true },
            ],
        });

        Assert.Equal(2, flipped.AllowedDirectories.Count);
        var a = flipped.AllowedDirectories.Single(rule => rule.Path == "C:\\proj\\a");
        var b = flipped.AllowedDirectories.Single(rule => rule.Path == "C:\\proj\\b");

        Assert.True(a.Enabled);
        Assert.Null(a.UpdatedAt);
        Assert.True(b.Enabled);
        Assert.NotNull(b.UpdatedAt);
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        _client?.Dispose();
    }

    public void Dispose()
    {
        _client?.Dispose();
    }

    private static readonly System.Text.Json.JsonSerializerOptions WebJson = new(
        System.Text.Json.JsonSerializerDefaults.Web);

    private static async Task<ShellConfig> GetConfigAsync(HttpClient client)
    {
        var config = await client.GetFromJsonAsync<ShellConfig>("/api/shell/config", WebJson);
        Assert.NotNull(config);
        return config!;
    }

    private static async Task<ShellConfig> PutConfigAsync(HttpClient client, ShellConfig config)
    {
        var response = await client.PutAsJsonAsync("/api/shell/config", config, WebJson);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<ShellConfig>(WebJson);
        Assert.NotNull(body);
        return body!;
    }

    private static ShellConfig ConfigWithDirectories(params AllowedDirectoryRule[] rules) => new(
        enabled: false,
        restrictToAllowedDirectories: true,
        allowedDirectories: [.. rules],
        blockedCommands: [],
        rootGuardrail: true,
        defaultTimeoutSeconds: 120,
        maxOutputBytes: 65536);

    private static AllowedDirectoryRule WhitelistEntry(string path, bool enabled) => new(
        path,
        enabled,
        createdAt: default,
        updatedAt: null);

    private static AllowedDirectoryRule BlankPathDirectory(string path) => WhitelistEntry(path, enabled: true);
}
