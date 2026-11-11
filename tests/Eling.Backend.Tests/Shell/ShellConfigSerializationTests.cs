using System.Text.Json;
using Eling.Backend.Shell;
using Eling.Core.Shell;
using Xunit;

namespace Eling.Backend.Tests.Shell;

/// <summary>
/// The shell configuration is written to <c>shell-config.json</c> and read back
/// by the tool, so its JSON shape is a contract: snake_case keys on the wire
/// (the same convention the tool policy file uses) and defaults that keep the
/// tool switched off until the user opts in.
/// </summary>
public sealed class ShellConfigSerializationTests
{
    /// <summary>
    /// The example configuration from the design spec, written with the
    /// snake_case key names the configuration file actually uses.
    /// </summary>
    private const string SpecExampleJson = """
        {
          "enabled": false,
          "restrict_to_allowed_directories": true,
          "allowed_directories": [
            {
              "path": "C:\\Projexts\\Shared",
              "enabled": true,
              "created_at": "2026-11-09T14:00:00Z",
              "updated_at": null
            }
          ],
          "blocked_commands": [
            {
              "pattern": "git checkout",
              "enabled": true,
              "created_at": "2026-11-09T14:00:00Z",
              "updated_at": null
            }
          ],
          "root_guardrail": true,
          "default_timeout_seconds": 120,
          "max_output_bytes": 65536,
          "updated_at": "2026-11-09T14:00:00Z"
        }
        """;

    private static readonly DateTimeOffset ExampleStamp = new(2026, 11, 9, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Deserialize_SpecExample_BindsEveryField()
    {
        var config = JsonSerializer.Deserialize(SpecExampleJson, ShellJsonContext.Default.ShellConfig);

        Assert.NotNull(config);
        Assert.False(config!.Enabled);
        Assert.True(config.RestrictToAllowedDirectories);
        Assert.True(config.RootGuardrail);
        Assert.Equal(120, config.DefaultTimeoutSeconds);
        Assert.Equal(65536, config.MaxOutputBytes);
        Assert.Equal(ExampleStamp, config.UpdatedAt);

        var directory = Assert.Single(config.AllowedDirectories);
        Assert.Equal("C:\\Projexts\\Shared", directory.Path);
        Assert.True(directory.Enabled);
        Assert.Equal(ExampleStamp, directory.CreatedAt);
        Assert.Null(directory.UpdatedAt);

        var blocked = Assert.Single(config.BlockedCommands);
        Assert.Equal("git checkout", blocked.Pattern);
        Assert.True(blocked.Enabled);
    }

    [Fact]
    public void Serialize_Config_WritesSnakeCaseKeys()
    {
        var config = new ShellConfig
        {
            AllowedDirectories = [new AllowedDirectoryRule { Path = "C:\\Projexts\\Shared" }],
        };

        var json = JsonSerializer.Serialize(config, ShellJsonContext.Default.ShellConfig);

        Assert.Contains("\"enabled\"", json);
        Assert.Contains("\"restrict_to_allowed_directories\"", json);
        Assert.Contains("\"allowed_directories\"", json);
        Assert.Contains("\"blocked_commands\"", json);
        Assert.Contains("\"root_guardrail\"", json);
        Assert.Contains("\"default_timeout_seconds\"", json);
        Assert.Contains("\"max_output_bytes\"", json);
        Assert.Contains("\"created_at\"", json);
        Assert.Contains("\"updated_at\"", json);
        Assert.DoesNotContain("\"restrictToAllowedDirectories\"", json);
        Assert.DoesNotContain("\"allowedDirectories\"", json);
        Assert.DoesNotContain("\"blockedCommands\"", json);
    }

    [Fact]
    public void Deserialize_MissingCollections_YieldsEmptyLists()
    {
        const string json = """
            { "enabled": true }
            """;

        var config = JsonSerializer.Deserialize(json, ShellJsonContext.Default.ShellConfig);

        Assert.NotNull(config);
        Assert.Empty(config!.BlockedCommands);
        Assert.Empty(config.AllowedDirectories);
    }

    /// <summary>
    /// A file that leaves a field out must not turn the sandbox or the root
    /// guardrail off: the source-generated JSON metadata assigns every
    /// property it knows about, so the defaults have to be applied by the
    /// model itself.
    /// </summary>
    [Fact]
    public void Deserialize_MissingScalars_KeepsTheSafeDefaults()
    {
        const string json = """
            { "enabled": true }
            """;

        var config = JsonSerializer.Deserialize(json, ShellJsonContext.Default.ShellConfig);

        Assert.NotNull(config);
        Assert.True(config!.RestrictToAllowedDirectories);
        Assert.True(config.RootGuardrail);
        Assert.Equal(120, config.DefaultTimeoutSeconds);
        Assert.Equal(65536, config.MaxOutputBytes);
    }

    /// <summary>
    /// A rule that the file does not switch off is active - a blocked command
    /// that silently defaulted to inactive would let the command run.
    /// </summary>
    [Fact]
    public void Deserialize_RuleWithoutEnabled_IsActive()
    {
        const string json = """
            { "blocked_commands": [ { "pattern": "git checkout" } ] }
            """;

        var config = JsonSerializer.Deserialize(json, ShellJsonContext.Default.ShellConfig);

        Assert.NotNull(config);
        var blocked = Assert.Single(config!.BlockedCommands);
        Assert.True(blocked.Enabled);
        Assert.Equal("git checkout", blocked.Pattern);
        Assert.Null(blocked.UpdatedAt);
    }

    [Fact]
    public void NewConfig_KeepsTheToolDisabledAndTheGuardrailsOn()
    {
        var before = DateTimeOffset.UtcNow.AddMinutes(-1);

        var config = new ShellConfig();

        Assert.False(config.Enabled);
        Assert.True(config.RestrictToAllowedDirectories);
        Assert.True(config.RootGuardrail);
        Assert.Equal(120, config.DefaultTimeoutSeconds);
        Assert.Equal(65536, config.MaxOutputBytes);
        Assert.Empty(config.AllowedDirectories);
        Assert.Empty(config.BlockedCommands);
        Assert.True(config.UpdatedAt >= before);
    }

    [Fact]
    public void NewRule_IsEnabledAndStamped()
    {
        var before = DateTimeOffset.UtcNow.AddMinutes(-1);

        var rule = new BlockedCommandRule { Pattern = "git checkout" };

        Assert.True(rule.Enabled);
        Assert.True(rule.CreatedAt >= before);
        Assert.True(rule.CreatedAt <= DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Null(rule.UpdatedAt);
    }

    /// <summary>
    /// The safe defaults only apply to the keys the file leaves out. A value
    /// the file states - including a <c>false</c> that switches a guardrail off
    /// - must survive deserialization unchanged.
    /// </summary>
    [Fact]
    public void Deserialize_ExplicitNonDefaultValues_AreHonored()
    {
        const string json = """
            {
              "restrict_to_allowed_directories": false,
              "root_guardrail": false,
              "default_timeout_seconds": 300,
              "max_output_bytes": 1024,
              "blocked_commands": [ { "pattern": "x", "enabled": false } ]
            }
            """;

        var config = JsonSerializer.Deserialize(json, ShellJsonContext.Default.ShellConfig);

        Assert.NotNull(config);
        Assert.False(config!.RestrictToAllowedDirectories);
        Assert.False(config.RootGuardrail);
        Assert.Equal(300, config.DefaultTimeoutSeconds);
        Assert.Equal(1024, config.MaxOutputBytes);

        var blocked = Assert.Single(config.BlockedCommands);
        Assert.Equal("x", blocked.Pattern);
        Assert.False(blocked.Enabled);
    }

    /// <summary>
    /// A rule the file states without any field must not blow up the whole
    /// configuration: the deserializer fills in the rule's own defaults. A
    /// null entry is accepted as well - the store is where such a rule gets
    /// dropped, so this test pins the current behaviour of the deserializer
    /// rather than promising a sanitized list.
    /// </summary>
    [Fact]
    public void Deserialize_RuleWithMissingAndNullEntries_DoesNotThrow()
    {
        const string missingFields = """
            { "blocked_commands": [ {} ] }
            """;
        const string nullEntry = """
            { "blocked_commands": [ null ] }
            """;

        var thrown = Record.Exception(() =>
            JsonSerializer.Deserialize(missingFields, ShellJsonContext.Default.ShellConfig));
        Assert.Null(thrown);

        var config = JsonSerializer.Deserialize(missingFields, ShellJsonContext.Default.ShellConfig);

        Assert.NotNull(config);
        var rule = Assert.Single(config!.BlockedCommands);
        Assert.NotNull(rule);
        Assert.True(string.IsNullOrEmpty(rule!.Pattern));

        var nullThrown = Record.Exception(() =>
            JsonSerializer.Deserialize(nullEntry, ShellJsonContext.Default.ShellConfig));
        Assert.Null(nullThrown);

        var withNull = JsonSerializer.Deserialize(nullEntry, ShellJsonContext.Default.ShellConfig);

        Assert.NotNull(withNull);
        Assert.Single(withNull!.BlockedCommands);
        Assert.Null(withNull.BlockedCommands[0]);
    }
}
