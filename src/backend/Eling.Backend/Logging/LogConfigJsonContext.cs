using System.Text.Json.Serialization;

namespace Eling.Backend.Logging;

/// <summary>
/// Source-generated JSON metadata for <see cref="LogConfigDto"/>. The backend
/// publishes with Native AOT, so every serialized type must be registered here.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LogConfigDto))]
public sealed partial class LogConfigJsonContext : JsonSerializerContext;
