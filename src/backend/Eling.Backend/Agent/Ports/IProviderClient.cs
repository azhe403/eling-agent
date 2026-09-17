namespace Eling.Backend.Agent.Ports;

public record ProviderProbe(bool Ok, string Message);

public interface IProviderClient
{
    Task<IReadOnlyList<string>> ListModelsAsync(string baseUrl, string? apiKey, CancellationToken ct);
    Task<ProviderProbe> TestConnectionAsync(string baseUrl, string? apiKey, string? model, CancellationToken ct);
}
