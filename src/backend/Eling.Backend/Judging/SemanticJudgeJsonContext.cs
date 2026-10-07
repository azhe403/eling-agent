using System.Text.Json.Serialization;

namespace Eling.Backend.Judging;

/// <summary>
/// Source-generated JSON metadata. The Backend is published with Native AOT, so
/// reflection-based <c>JsonSerializer.Deserialize&lt;T&gt;</c> is unavailable and every
/// serialised type must be registered here.
/// </summary>
/// <remarks>
/// Case-insensitive and indented on purpose: this file is hand-edited before a settings
/// screen exists, so it should be readable and forgiving. A single stray capital letter
/// would otherwise leave the judge silently switched off with no error anywhere, and a
/// minified blob is nobody's idea of a config file.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SemanticJudgeConfig))]
[JsonSerializable(typeof(JudgeVerdict))]
public sealed partial class SemanticJudgeJsonContext : JsonSerializerContext;
