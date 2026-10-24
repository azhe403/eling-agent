using System.Text.Json;

namespace Eling.Core.Serialization;

/// <summary>
/// Unwraps a <see cref="JsonElement"/> into a plain CLR value so that a value
/// cached as JSON is not handed back as a <see cref="JsonElement"/> after the
/// cache file is re-read. Numbers come back as <see cref="long"/> or
/// <see cref="double"/>, booleans as <see cref="bool"/>, strings as
/// <see cref="string"/>, and null as <see langword="null"/>.
/// </summary>
public static class JsonCell
{
    public static object? ToClrValue(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;

            case JsonValueKind.True:
                return true;

            case JsonValueKind.False:
                return false;

            case JsonValueKind.Number:
                if (element.TryGetInt64(out var longValue))
                {
                    return longValue;
                }

                return element.GetDouble();

            case JsonValueKind.String:
                return element.GetString();

            case JsonValueKind.Array:
            case JsonValueKind.Object:
                // Keep the raw JSON for anything that is not a scalar. These
                // values are only produced by providers that return structured
                // data, which is uncommon for a database cell.
                return element;

            default:
                return null;
        }
    }
}