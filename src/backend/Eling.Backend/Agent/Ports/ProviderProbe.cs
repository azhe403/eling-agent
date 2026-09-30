namespace Eling.Backend.Agent.Ports;

/// <summary>
/// Outcome of a one-shot connectivity check against a provider endpoint.
/// </summary>
/// <remarks>
/// Shares its shape with the REST-level <c>ProviderTestResponse</c>; the two are
/// kept apart because they live on opposite sides of the API boundary.
/// </remarks>
public record ProviderProbe(
    bool Ok,
    string Message);
