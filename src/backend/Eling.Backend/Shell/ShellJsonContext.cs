using System.Text.Json.Serialization;
using Eling.Core.Shell;

namespace Eling.Backend.Shell;

/// <summary>
/// Source-generated JSON metadata for the shell configuration. The backend
/// publishes with Native AOT, so every serialized type must be registered here.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ShellConfig))]
public sealed partial class ShellJsonContext : JsonSerializerContext;
