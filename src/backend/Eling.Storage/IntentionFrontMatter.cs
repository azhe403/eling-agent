using YamlDotNet.Serialization;

namespace Eling.Storage;

internal class IntentionFrontMatter
{
    [YamlMember(Alias = "id")]
    public string Id { get; set; } = string.Empty;

    [YamlMember(Alias = "description")]
    public string Description { get; set; } = string.Empty;

    [YamlMember(Alias = "trigger_type")]
    public string TriggerType { get; set; } = string.Empty;

    [YamlMember(Alias = "status")]
    public string Status { get; set; } = string.Empty;

    [YamlMember(Alias = "created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [YamlMember(Alias = "updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }

    [YamlMember(Alias = "expires_at")]
    public DateTimeOffset? ExpiresAt { get; set; }

    [YamlMember(Alias = "source")]
    public string? Source { get; set; }

    [YamlMember(Alias = "tags")]
    public List<string>? Tags { get; set; }
}