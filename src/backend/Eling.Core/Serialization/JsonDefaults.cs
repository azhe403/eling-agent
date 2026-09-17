using System.Text.Json;

namespace Eling.Core.Serialization;

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Shared = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}
