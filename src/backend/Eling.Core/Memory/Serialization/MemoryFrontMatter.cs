using YamlDotNet.Serialization;

namespace Eling.Core;

internal class MemoryFrontMatter
{
    [YamlMember(Alias = "id")]
    public string Id { get; set; } = string.Empty;

    [YamlMember(Alias = "type")]
    public string Type { get; set; } = string.Empty;

    [YamlMember(Alias = "status")]
    public string Status { get; set; } = string.Empty;

    [YamlMember(Alias = "tags")]
    public List<string>? Tags { get; set; }

    [YamlMember(Alias = "created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [YamlMember(Alias = "updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }

    [YamlMember(Alias = "source")]
    public string? Source { get; set; }
}

