namespace Eling.Desktop.Models;

public record ProviderViewDto(string? BaseUrl, string? Model, bool HasKey, List<string> ModelsCached);

public record ModelListDto(List<string> Models);

public record ProviderTestResultDto(bool Ok, string Message);
