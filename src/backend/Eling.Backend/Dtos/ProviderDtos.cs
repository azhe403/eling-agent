namespace Eling.Backend.Dtos;

public record ProviderView(string? BaseUrl, string? Model, bool HasKey, List<string> ModelsCached);

public record UpdateProviderRequest(string? BaseUrl, string? Model, string? ApiKey);

public record ModelListResponse(List<string> Models);

public record ProviderTestResponse(bool Ok, string Message);
