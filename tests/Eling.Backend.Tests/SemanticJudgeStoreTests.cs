using Eling.Backend.Judging;
using Eling.Core.Scope;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Eling.Backend.Tests;
/// <summary>
/// The judge config is per-user and holds an API key, so these tests care about two
/// things: the key is never handed back out, and a half-configured or corrupt file
/// degrades to "off" instead of failing a save.
/// </summary>
public sealed class SemanticJudgeStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"eling-judge-{Guid.NewGuid():N}");

    private SemanticJudgeStore NewStore()
    {
        Directory.CreateDirectory(_root);
        return new SemanticJudgeStore(new UserScope(_root), NullLogger<SemanticJudgeStore>.Instance);
    }

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
    public void GetView_FreshStore_IsOffAndNotConfigured()
    {
        var view = NewStore().GetView();

        Assert.False(view.Enabled);
        Assert.False(view.HasApiKey);
        Assert.False(view.IsConfigured);
    }

    [Fact]
    public void Update_ReportsAStoredKeyWithoutExposingIt()
    {
        var store = NewStore();

        var view = store.Update(enabled: true, baseUrl: "https://example.test/v1", model: "judge-1", apiKey: "secret-value");

        Assert.True(view.HasApiKey);
        Assert.True(view.IsConfigured);
        Assert.Equal("https://example.test/v1", view.BaseUrl);
        Assert.Equal("judge-1", view.Model);
        // The view has no ApiKey member at all, so nothing can leak it by accident.
        Assert.DoesNotContain(
            typeof(SemanticJudgeView).GetProperties(),
            property => property.Name.Contains("ApiKey", StringComparison.OrdinalIgnoreCase) && property.PropertyType == typeof(string));
    }

    [Fact]
    public void Update_EmptyApiKey_LeavesTheStoredKeyInPlace()
    {
        var store = NewStore();
        store.Update(enabled: true, baseUrl: "https://example.test/v1", model: "judge-1", apiKey: "secret-value");

        var view = store.Update(model: "judge-2", apiKey: "");

        Assert.True(view.HasApiKey);
        Assert.True(store.TryGetConfig(out _, out var model, out var apiKey));
        Assert.Equal("judge-2", model);
        Assert.Equal("secret-value", apiKey);
    }

    [Fact]
    public void TryGetConfig_EnabledButMissingModel_IsRefused()
    {
        var store = NewStore();
        store.Update(enabled: true, baseUrl: "https://example.test/v1", model: null, apiKey: "secret-value");

        Assert.False(store.TryGetConfig(out _, out _, out _));
    }

    [Fact]
    public void TryGetConfig_Disabled_IsRefused()
    {
        var store = NewStore();
        store.Update(enabled: false, baseUrl: "https://example.test/v1", model: "judge-1", apiKey: "secret-value");

        Assert.False(store.TryGetConfig(out _, out _, out _));
    }

    [Fact]
    public void TryGetConfig_EnabledAndComplete_Succeeds()
    {
        var store = NewStore();
        store.Update(enabled: true, baseUrl: "https://example.test/v1", model: "judge-1", apiKey: "secret-value");

        Assert.True(store.TryGetConfig(out var baseUrl, out var model, out var apiKey));
        Assert.Equal("https://example.test/v1", baseUrl);
        Assert.Equal("judge-1", model);
        Assert.Equal("secret-value", apiKey);
    }

    [Fact]
    public void Config_SurvivesANewStoreInstance()
    {
        NewStore().Update(enabled: true, baseUrl: "https://example.test/v1", model: "judge-1", apiKey: "secret-value");

        Assert.True(NewStore().GetView().IsConfigured);
    }

    [Fact]
    public void CorruptConfigFile_DegradesToOffInsteadOfThrowing()
    {
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        File.WriteAllText(Path.Combine(_root, "config", "semantic-judge.json"), "{ not json");

        var store = NewStore();

        Assert.False(store.GetView().IsConfigured);
        Assert.False(store.TryGetConfig(out _, out _, out _));
    }

    [Fact]
    public void HandWrittenConfig_MixedCasing_IsStillRead()
    {
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        File.WriteAllText(
            Path.Combine(_root, "config", "semantic-judge.json"),
            """{ "Enabled": true, "BaseUrl": "https://example.test/v1", "Model": "judge-1", "ApiKey": "k" }""");

        Assert.True(NewStore().GetView().IsConfigured);
    }

    [Fact]
    public void ConfigFile_EndsUpInTheUserConfigDirectory()
    {
        NewStore().Update(enabled: true, baseUrl: "https://example.test/v1", model: "judge-1", apiKey: "k");

        // Per-user on purpose: a judge key must never sit inside a project's .eling,
        // which is tracked by git.
        Assert.True(File.Exists(Path.Combine(_root, "config", "semantic-judge.json")));
    }

    [Fact]
    public void FirstRun_WritesATemplateSoTheKeysAreDiscoverable()
    {
        _ = NewStore();

        var path = Path.Combine(_root, "config", "semantic-judge.json");
        Assert.True(File.Exists(path));

        // All four keys must be present: an all-null config would serialise to just
        // {"enabled":false} and tell whoever opens the file nothing.
        var raw = File.ReadAllText(path);
        Assert.Contains("\"enabled\"", raw);
        Assert.Contains("\"baseUrl\"", raw);
        Assert.Contains("\"model\"", raw);
        Assert.Contains("\"apiKey\"", raw);
    }

    [Fact]
    public void FirstRun_TemplateReadsAsOffAndNotConfigured()
    {
        var view = NewStore().GetView();

        Assert.False(view.Enabled);
        Assert.False(view.IsConfigured);
        Assert.False(view.HasApiKey);
    }

    [Fact]
    public void Template_DoesNotOverwriteAnExistingConfig()
    {
        NewStore().Update(enabled: true, baseUrl: "https://example.test/v1", model: "judge-1", apiKey: "secret-value");

        Assert.True(NewStore().GetView().IsConfigured);
    }

    [Fact]
    public void DeletedConfig_IsRecreatedOnTheNextRun()
    {
        _ = NewStore();
        var path = Path.Combine(_root, "config", "semantic-judge.json");
        File.Delete(path);

        _ = NewStore();

        Assert.True(File.Exists(path));
        Assert.False(NewStore().GetView().IsConfigured);
    }

    [Fact]
    public void CorruptConfig_IsNotOverwrittenSoItCanBeInspected()
    {
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        var path = Path.Combine(_root, "config", "semantic-judge.json");
        File.WriteAllText(path, "{ not json");

        _ = NewStore();

        Assert.Equal("{ not json", File.ReadAllText(path));
    }
}
