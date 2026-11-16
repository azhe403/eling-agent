using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Eling.Backend.Bootstrap;
using Eling.Core.Scope;
using Microsoft.AspNetCore.Builder;

namespace Eling.Backend.Tests;

/// <summary>
/// Integration tests for the skills REST API. Same harness as
/// <see cref="MemoryApiTests"/>: a self-contained host over a throwaway
/// data directory and a throwaway <c>ELING_SKILLS_DIR</c> catalog (set by
/// <see cref="TestAppBuilder.CreateSelfContained"/>), so the tests never
/// touch the real machine catalog. Each test method gets a fresh host.
/// </summary>
[Collection(ElingDataDirCollection.Name)]
public class SkillApiTests : IAsyncLifetime, IDisposable
{
    private readonly string _tempDir;
    private WebApplication? _app;
    private HttpClient? _client;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public SkillApiTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "eling-tests-" + Guid.NewGuid().ToString("N")[..8]);
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);

        // Pick a free random port so the test doesn't conflict with the dev backend.
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        _app = TestAppBuilder.CreateSelfContained(dashboardPort: port);
        await _app.StartAsync();
        _client = new HttpClient(new EnvelopeTestHandler { InnerHandler = new HttpClientHandler() })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}")
        };
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
            try { await _app.StopAsync(); }
            catch (Exception ex) { Console.WriteLine($"StopAsync failed during test-host teardown: {ex.Message}"); }
            try { await _app.DisposeAsync(); }
            catch (Exception ex) { Console.WriteLine($"DisposeAsync failed during test-host teardown: {ex.Message}"); }
            _app = null;
        }
    }

    public void Dispose()
    {
        TryDeleteDirectory(_tempDir);
        GC.SuppressFinalize(this);
    }

    private HttpClient EnsureClient() =>
        _client ?? throw new InvalidOperationException(
            "Test host failed to initialize before the test method ran.");

    private static string CatalogRoot =>
        Environment.GetEnvironmentVariable(SkillCatalogPaths.SkillsDirEnvironmentVariable)
            ?? throw new InvalidOperationException("ELING_SKILLS_DIR is not set by the test harness.");

    private static string SkillsDirectory => Path.Combine(CatalogRoot, SkillCatalogPaths.SkillsSubdir);

    private static void TryDeleteDirectory(string path, int retries = 5, int delayMs = 200)
    {
        if (!Directory.Exists(path)) return;

        for (var i = 0; i < retries; i++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(delayMs);
            }
        }
    }

    private static async Task<JsonElement> PostSkillAsync(HttpClient client, object payload)
    {
        var response = await client.PostAsJsonAsync("/api/skills", payload);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
    }

    [Fact]
    public async Task GetApiSkills_ReturnsItemsAndMetadata()
    {
        var client = EnsureClient();
        await PostSkillAsync(client, new { name = "alpha-skill", description = "first", body = "# alpha\n\nA." });
        await PostSkillAsync(client, new { name = "beta-skill", description = "second", body = "# beta\n\nB." });

        var response = await client.GetAsync("/api/skills?page=1&pageSize=10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal(2, body.GetProperty("items").GetArrayLength());
        var metadata = body.GetProperty("metadata");
        Assert.Equal(2, metadata.GetProperty("total").GetInt32());
        Assert.Equal(2, metadata.GetProperty("matching").GetInt32());
        Assert.Equal(10, metadata.GetProperty("limit").GetInt32());
        Assert.Equal(0, metadata.GetProperty("offset").GetInt32());

        // The raw (non-unwrapping) client sees the standard envelope around the list.
        using var raw = new HttpClient { BaseAddress = client.BaseAddress };
        var rawResponse = await raw.GetAsync("/api/skills?page=1&pageSize=10");
        Assert.Equal(HttpStatusCode.OK, rawResponse.StatusCode);
        var envelope = await rawResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("ok", envelope.GetProperty("code").GetString());
        var data = envelope.GetProperty("data");
        Assert.Equal(2, data.GetProperty("items").GetArrayLength());
        Assert.Equal(2, data.GetProperty("metadata").GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task PostSkill_CreatesAndGetsBack()
    {
        var client = EnsureClient();
        var response = await client.PostAsJsonAsync("/api/skills", new
        {
            name = "created-skill",
            description = "roundtrip",
            body = "# created\n\nBody content here.",
            tags = new[] { "alpha", "beta" }
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var id = created.GetProperty("id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(id));
        Assert.Equal($"/api/skills/{id}", response.Headers.Location?.OriginalString);
        Assert.Equal("eling", created.GetProperty("origin").GetString());

        var getResponse = await client.GetAsync($"/api/skills/{id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var skill = await getResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("created-skill", skill.GetProperty("name").GetString());
        Assert.Equal("roundtrip", skill.GetProperty("description").GetString());
        Assert.Equal("# created\n\nBody content here.", skill.GetProperty("body").GetString());
    }

    [Fact]
    public async Task PutSkill_UpdatesDescriptionAndRenameKeepsId()
    {
        var client = EnsureClient();
        var created = await PostSkillAsync(client, new
        {
            name = "original-skill",
            description = "before rename",
            body = "# original\n\nO."
        });
        var id = created.GetProperty("id").GetString()!;

        var putResponse = await client.PutAsJsonAsync($"/api/skills/{id}", new
        {
            name = "renamed-skill",
            description = "after rename"
        });
        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);

        var updated = await putResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal(id, updated.GetProperty("id").GetString());
        Assert.Equal("renamed-skill", updated.GetProperty("name").GetString());
        Assert.Equal("after rename", updated.GetProperty("description").GetString());

        // The folder moved with the rename.
        Assert.False(Directory.Exists(Path.Combine(SkillsDirectory, "original-skill")));
        Assert.True(File.Exists(Path.Combine(SkillsDirectory, "renamed-skill", "SKILL.md")));

        // GetById still resolves the same id to the renamed skill.
        var getResponse = await client.GetAsync($"/api/skills/{id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var skill = await getResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("renamed-skill", skill.GetProperty("name").GetString());
        Assert.Equal(id, skill.GetProperty("id").GetString());
    }

    [Fact]
    public async Task PutSkill_ExternalForbidden()
    {
        var client = EnsureClient();
        await SeedForeignSkillAsync("foreign-skill");
        var rescan = await client.PostAsync("/api/skills/rescan", null);
        Assert.Equal(HttpStatusCode.OK, rescan.StatusCode);

        var listResponse = await client.GetAsync("/api/skills?origin=external");
        var list = await listResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var externalItem = list.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("name").GetString() == "foreign-skill");
        var id = externalItem.GetProperty("id").GetString()!;

        var putResponse = await client.PutAsJsonAsync($"/api/skills/{id}", new { description = "hijacked" });
        Assert.Equal(HttpStatusCode.Conflict, putResponse.StatusCode);

        // The failure envelope carries the machine code; 4xx bodies are not unwrapped.
        var failure = await putResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("skill_read_only", failure.GetProperty("code").GetString());
    }

    [Fact]
    public async Task DeleteSkill_External_MovesToTrashThenRestoreBringsItBack()
    {
        var client = EnsureClient();
        await SeedForeignSkillAsync("foreign-delete");
        var rescan = await client.PostAsync("/api/skills/rescan", null);
        Assert.Equal(HttpStatusCode.OK, rescan.StatusCode);

        var listResponse = await client.GetAsync("/api/skills?origin=external");
        var list = await listResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var externalItem = list.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("name").GetString() == "foreign-delete");
        var id = externalItem.GetProperty("id").GetString()!;

        var deleteResponse = await client.DeleteAsync($"/api/skills/{id}");
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);
        var trashed = await deleteResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.True(trashed.GetProperty("trashed").GetBoolean());

        // Gone from the active catalog, visible in trash mode (origin-agnostic).
        var activeList = await client.GetAsync("/api/skills?origin=external");
        var activeItems = (await activeList.Content.ReadFromJsonAsync<JsonElement>(JsonOptions))
            .GetProperty("items").EnumerateArray().ToList();
        Assert.DoesNotContain(activeItems, i => i.GetProperty("name").GetString() == "foreign-delete");

        var trashList = await client.GetAsync("/api/skills?origin=external&includeTrashed=true");
        var trashItems = (await trashList.Content.ReadFromJsonAsync<JsonElement>(JsonOptions))
            .GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(trashItems, i => i.GetProperty("name").GetString() == "foreign-delete");

        // POST restore resurrects the externally-supplied folder untouched.
        var restoreResponse = await client.PostAsync($"/api/skills/{id}/restore", null);
        Assert.Equal(HttpStatusCode.OK, restoreResponse.StatusCode);
        var restored = await restoreResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal(id, restored.GetProperty("id").GetString());
        Assert.Equal("foreign-delete", restored.GetProperty("name").GetString());
        Assert.Equal("external", restored.GetProperty("origin").GetString());
        Assert.True(File.Exists(Path.Combine(SkillsDirectory, "foreign-delete", "SKILL.md")));
    }

    [Fact]
    public async Task DeleteSkill_MovesToTrashThenRestoreBringsItBack()
    {
        var client = EnsureClient();
        var created = await PostSkillAsync(client, new
        {
            name = "ephemeral-skill",
            description = "here today",
            body = "# ephemeral\n\nE."
        });
        var id = created.GetProperty("id").GetString()!;

        var deleteResponse = await client.DeleteAsync($"/api/skills/{id}");
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);
        var trashed = await deleteResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.True(trashed.GetProperty("trashed").GetBoolean());

        var getAfterDelete = await client.GetAsync($"/api/skills/{id}");
        Assert.Equal(HttpStatusCode.NotFound, getAfterDelete.StatusCode);

        var restoreResponse = await client.PostAsync($"/api/skills/{id}/restore", null);
        Assert.Equal(HttpStatusCode.OK, restoreResponse.StatusCode);
        var restored = await restoreResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal(id, restored.GetProperty("id").GetString());
        Assert.Equal("ephemeral-skill", restored.GetProperty("name").GetString());

        var getAfterRestore = await client.GetAsync($"/api/skills/{id}");
        Assert.Equal(HttpStatusCode.OK, getAfterRestore.StatusCode);
        var skill = await getAfterRestore.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("ephemeral-skill", skill.GetProperty("name").GetString());
    }

    [Fact]
    public async Task PostRescan_IndexesNewExternalSkill()
    {
        var client = EnsureClient();
        await SeedForeignSkillAsync("rescan-external");

        var rescanResponse = await client.PostAsync("/api/skills/rescan", null);
        Assert.Equal(HttpStatusCode.OK, rescanResponse.StatusCode);
        var report = await rescanResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.True(report.TryGetProperty("discovered", out _));
        Assert.True(report.TryGetProperty("external", out _));
        Assert.True(report.TryGetProperty("missing", out _));
        Assert.True(report.TryGetProperty("failed", out _));
        Assert.True(report.TryGetProperty("renamed", out _));

        var listResponse = await client.GetAsync("/api/skills?origin=external");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var list = await listResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var names = list.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("name").GetString())
            .ToList();
        Assert.Contains("rescan-external", names);
    }

    /// <summary>
    /// Drops a foreign (non-Eling, no frontmatter) SKILL.md folder into the
    /// isolated catalog — the exact shape the scanner indexes as external.
    /// </summary>
    private static async Task SeedForeignSkillAsync(string name)
    {
        var folder = Path.Combine(SkillsDirectory, name);
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(
            Path.Combine(folder, "SKILL.md"),
            $"# {name}\n\nExternally supplied skill seeded by a test; Eling must treat it as read-only.\n");
    }
}
