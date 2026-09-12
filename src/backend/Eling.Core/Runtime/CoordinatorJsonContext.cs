using System.Text.Json.Serialization;

namespace Eling.Core.Runtime;

// Source generator context for AOT-friendly serialization
[JsonSerializable(typeof(RuntimeRegistration))]
[JsonSerializable(typeof(List<RuntimeInfo>))]
public partial class CoordinatorJsonContext : JsonSerializerContext;
