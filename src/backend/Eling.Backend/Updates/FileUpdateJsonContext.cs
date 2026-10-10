using System.Text.Json.Serialization;

namespace Eling.Backend.Updates;

/// <summary>
/// Source-generated JSON metadata for the update cache. Mirrors
/// <see cref="Tools.ToolPolicyJsonContext"/>: snake_case on the wire,
/// case-insensitive read, indented write, and source generation so the type
/// stays trimming/AOT safe instead of relying on reflection.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true)]
[JsonSerializable(typeof(UpdateStatus))]
public sealed partial class FileUpdateJsonContext : JsonSerializerContext;
