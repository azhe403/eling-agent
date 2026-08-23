using System.Text.Json.Serialization;

namespace Eling.Host;

public sealed class CoordinatorClaim
{
    [JsonPropertyName("processId")]
    public int ProcessId { get; set; }

    [JsonPropertyName("startedAt")]
    public DateTimeOffset StartedAt { get; set; }

    [JsonPropertyName("port")]
    public int Port { get; set; } = 4317;
}

// Source generator context for AOT-friendly serialization
[JsonSerializable(typeof(RuntimeRegistration))]
[JsonSerializable(typeof(CoordinatorClaim))]
[JsonSerializable(typeof(List<RuntimeInfo>))]
public partial class CoordinatorJsonContext : JsonSerializerContext;
