using Eling.Core.Logging;
using Serilog.Events;
using Serilog.Parsing;

namespace Eling.Core.Tests;

/// <summary>
/// Covers the precedence rule <see cref="AspNetEnvironmentEnricher"/> applies
/// when it mirrors the ASP.NET Core generic host. The rule is reproduced rather
/// than delegated, so it is the one part of this feature that can go quietly
/// wrong — a change in the host's own ordering would leave the log quietly
/// reporting the wrong environment.
/// </summary>
public sealed class AspNetEnvironmentEnricherTests
{
    [Theory]
    [InlineData("Staging", "Production", "Staging")]
    [InlineData("Development", "Staging", "Development")]
    public void AspNetCore_variable_outranks_DotNet_variable(
        string aspNetCoreEnvironment,
        string dotnetEnvironment,
        string expected)
    {
        var actual = AspNetEnvironmentEnricher.Resolve(aspNetCoreEnvironment, dotnetEnvironment);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Falls_back_to_DotNet_variable_when_AspNetCore_is_unset()
    {
        var actual = AspNetEnvironmentEnricher.Resolve(null, "Staging");

        Assert.Equal("Staging", actual);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("   ", "\t")]
    [InlineData("", "Development")]
    [InlineData("  ", "Development")]
    public void Blank_AspNetCore_value_falls_through_instead_of_producing_an_empty_name(
        string? aspNetCoreEnvironment,
        string? dotnetEnvironment)
    {
        var actual = AspNetEnvironmentEnricher.Resolve(aspNetCoreEnvironment, dotnetEnvironment);

        Assert.False(string.IsNullOrWhiteSpace(actual), "An empty environment name is unreadable in a log line.");
    }

    [Fact]
    public void Defaults_to_Production_when_nothing_is_set()
    {
        var actual = AspNetEnvironmentEnricher.Resolve(null, null);

        Assert.Equal("Production", actual);
    }

    [Fact]
    public void Enrich_writes_the_resolved_name_onto_the_event()
    {
        var enricher = new AspNetEnvironmentEnricher("Staging");
        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            exception: null,
            new MessageTemplateParser().Parse("hello"),
            []);

        enricher.Enrich(logEvent, new StubPropertyFactory());

        Assert.True(
            logEvent.Properties.TryGetValue(AspNetEnvironmentEnricher.PropertyName, out var value),
            $"Expected property {AspNetEnvironmentEnricher.PropertyName} on the event.");

        // Compare the underlying value, not ToString(): a ScalarValue holding a
        // string renders with quotes, so asserting on the rendered form would
        // pin the test to formatting rather than to the property's content.
        var scalar = Assert.IsType<ScalarValue>(value);
        Assert.Equal("Staging", scalar.Value);
    }

    [Fact]
    public void Enrich_does_not_overwrite_a_name_pushed_by_a_scope()
    {
        var enricher = new AspNetEnvironmentEnricher("Production");
        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            exception: null,
            new MessageTemplateParser().Parse("hello"),
            []);
        logEvent.AddPropertyIfAbsent(
            new StubPropertyFactory().CreateProperty(AspNetEnvironmentEnricher.PropertyName, "DeliberateOverride"));

        enricher.Enrich(logEvent, new StubPropertyFactory());

        var scalar = Assert.IsType<ScalarValue>(logEvent.Properties[AspNetEnvironmentEnricher.PropertyName]);
        Assert.Equal("DeliberateOverride", scalar.Value);
    }

    private sealed class StubPropertyFactory : Serilog.Core.ILogEventPropertyFactory
    {
        // A ScalarValue, not the raw object: the log template only ever formats
        // the rendered value, so anything but a real LogEventPropertyValue here
        // would not exercise the path a live sink takes.
        public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false)
            => new(name, new ScalarValue(value));
    }
}
