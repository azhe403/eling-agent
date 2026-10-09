using System.Text.Json.Serialization;
using Eling.Core.Tools;

namespace Eling.Backend.Tools;

/// <summary>
/// Source-generated JSON metadata. The backend publishes with Native AOT, so
/// every serialized type must be registered here.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ToolPolicyConfig))]
public sealed partial class ToolPolicyJsonContext : JsonSerializerContext;
