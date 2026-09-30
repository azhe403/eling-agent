using Serilog.Core;
using Serilog.Events;

namespace Eling.Core.Logging;

/// <summary>
/// Stamps every log event with the current ASP.NET Core environment name so a
/// line can be attributed to the environment it was produced under, not just to
/// the process.
/// </summary>
/// <remarks>
/// <para>
/// The value is resolved ONCE in the constructor, not per event. Serilog calls
/// <see cref="Enrich"/> for every single log event, and a Debug-level sink writes
/// hundreds of thousands of lines per run; reading the environment on each call
/// would put two environment lookups on that hot path for a value that cannot
/// change while the process is alive.
/// </para>
/// <para>
/// The lookup order mirrors the ASP.NET Core generic host: an
/// <c>ASPNETCORE_ENVIRONMENT</c>-prefixed variable outranks a
/// <c>DOTNET_ENVIRONMENT</c>-prefixed one, because the host consults the
/// narrower prefix first. Unset on both sides means <c>Production</c>, which is
/// the host's own default and not a guess.
/// </para>
/// <para>
/// Whitespace-only values are treated as unset, which is a deliberate deviation
/// from the host (an empty variable would reach it as an empty name). The rest
/// of this codebase already normalises on <see cref="string.IsNullOrWhiteSpace"/>
/// when reading configuration out of the environment, and an empty environment
/// name in a log line is unreadable rather than informative.
/// </para>
/// </remarks>
public sealed class AspNetEnvironmentEnricher : ILogEventEnricher
{
    /// <summary>The log-event property this enricher writes.</summary>
    public const string PropertyName = "AspNetEnvironment";

    /// <summary>The name the ASP.NET Core host falls back to when nothing is set.</summary>
    private const string DefaultEnvironmentName = "Production";

    private const string AspNetCoreVariable = "ASPNETCORE_ENVIRONMENT";
    private const string DotNetVariable = "DOTNET_ENVIRONMENT";

    private readonly string _environmentName;

    /// <summary>
    /// Creates the enricher from the current process environment.
    /// </summary>
    public AspNetEnvironmentEnricher()
        : this(Resolve(Environment.GetEnvironmentVariable(AspNetCoreVariable),
                       Environment.GetEnvironmentVariable(DotNetVariable)))
    {
    }

    /// <summary>
    /// Creates the enricher from an already-resolved name. Exists so the
    /// precedence rule can be exercised without mutating the real process
    /// environment.
    /// </summary>
    public AspNetEnvironmentEnricher(string environmentName)
    {
        _environmentName = environmentName;
    }

    /// <summary>
    /// Resolves the environment name the ASP.NET Core host would report, given
    /// the raw variable values. <paramref name="aspNetCoreEnvironment"/> wins
    /// over <paramref name="dotnetEnvironment"/>; a blank value at either level
    /// falls through to the next.
    /// </summary>
    public static string Resolve(string? aspNetCoreEnvironment, string? dotnetEnvironment)
    {
        if (!string.IsNullOrWhiteSpace(aspNetCoreEnvironment))
        {
            return aspNetCoreEnvironment;
        }

        return string.IsNullOrWhiteSpace(dotnetEnvironment)
            ? DefaultEnvironmentName
            : dotnetEnvironment;
    }

    /// <inheritdoc />
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        // AddPropertyIfAbsent, not AddProperty: a scope that deliberately pushes
        // its own AspNetEnvironment must win over this process-wide default.
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(PropertyName, _environmentName));
    }
}
