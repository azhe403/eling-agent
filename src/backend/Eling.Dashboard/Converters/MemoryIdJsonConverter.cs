using System.Text.Json;
using System.Text.Json.Serialization;
using Eling.Core;

namespace Eling.Dashboard.Converters;

public sealed class MemoryIdJsonConverter : JsonConverter<MemoryId>
{
    public override MemoryId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString() ?? throw new JsonException("MemoryId cannot be null");
        return MemoryId.Parse(value);
    }

    public override void Write(Utf8JsonWriter writer, MemoryId value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}