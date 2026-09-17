using System.Text.Json;
using System.Text.Json.Serialization;
using Eling.Backend.Dtos;

namespace Eling.Backend.Tests;

public class AgentContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public void ProviderView_SerializesCamelCase_AndNeverCarriesKey()
    {
        var json = JsonSerializer.Serialize(new ProviderView("http://127.0.0.1:11434/v1", "qwen3", true, ["qwen3"]), JsonOptions);
        Assert.Contains("\"baseUrl\"", json);
        Assert.Contains("\"hasKey\"", json);
        Assert.DoesNotContain("apiKey", json);
    }

    [Fact]
    public void TurnResponse_RoundTrips()
    {
        var original = new TurnResponse("c1", "hello", [new ToolCallDto("read_file", "{}")]);
        var back = JsonSerializer.Deserialize<TurnResponse>(JsonSerializer.Serialize(original, JsonOptions), JsonOptions);
        Assert.NotNull(back);
        Assert.Equal("c1", back.ChatId);
        Assert.Single(back.ToolCalls);
    }
}
