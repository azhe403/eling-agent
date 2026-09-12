using System.Text.Json.Serialization;

namespace Eling.Backend.Dtos;

/// <summary>
/// Lean project-scope posture surfaced by <c>memory_recall</c>. When
/// <see cref="Adoptable"/> is true the agent may offer consent-gated onboarding;
/// when <see cref="Policy"/> is <c>disabled</c> it must not, and should use
/// <c>scope=global</c>.
/// </summary>
public sealed class MemoryRecallProjectScopeDto
{
    [JsonPropertyName("posture")]
    public string Posture { get; set; } = "uninitialized";

    [JsonPropertyName("policy")]
    public string Policy { get; set; } = "ask";

    [JsonPropertyName("adoptable")]
    public bool Adoptable { get; set; }

    [JsonPropertyName("initialized")]
    public bool Initialized { get; set; }

    [JsonPropertyName("headRoot")]
    public string? HeadRoot { get; set; }
}
