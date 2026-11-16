using System.Text.Json.Serialization;

namespace Eling.Backend.Dtos;

/// <summary>
/// Input shape for one entry in the skill_save siblingFiles array: a path
/// relative to the skill folder (forward slashes, no parent traversal) and
/// the file content Base64-encoded. The tool layer decodes and validates
/// every entry before any filesystem write happens.
/// </summary>
public sealed class SkillSiblingFileInput
{
    [JsonPropertyName("relativePath")]
    public string RelativePath { get; set; } = "";

    [JsonPropertyName("contentBase64")]
    public string ContentBase64 { get; set; } = "";
}
