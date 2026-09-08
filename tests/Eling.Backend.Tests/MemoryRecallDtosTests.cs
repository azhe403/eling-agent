using System.Text.Json;
using Eling.Backend.Dtos;
using Eling.Core;

namespace Eling.Backend.Tests;

public class MemoryRecallDtosTests
{
    [Fact]
    public void From_MemoryRecallHit_CarriesMatchedViaAndScores()
    {
        var memory = new Memory(MemoryType.Fact, "Test content", tags: new[] { "git", "hygiene" });
        var hit = new MemoryRecallHit(
            memory,
            MatchedVia: new[] { "porter", "trigram" },
            PorterScore: 1.2,
            TrigramScore: 0.8);

        var dto = MemoryRecallMemory.From(hit);

        Assert.Equal(memory.Id, dto.Id);
        Assert.Equal(new[] { "porter", "trigram" }, dto.MatchedVia);
        Assert.Equal(1.2, dto.PorterScore);
        Assert.Equal(0.8, dto.TrigramScore);
    }

    [Fact]
    public void From_PlainMemory_LeavesMatchedViaNull()
    {
        var memory = new Memory(MemoryType.Fact, "Test content");

        var dto = MemoryRecallMemory.From(memory);

        Assert.Null(dto.MatchedVia);
        Assert.Equal(0.0, dto.PorterScore);
        Assert.Equal(0.0, dto.TrigramScore);
    }

    [Fact]
    public void Serialize_MemoryRecallMemory_IncludesMatchedViaJsonProperty()
    {
        var memory = new Memory(MemoryType.Fact, "Test content");
        var dto = MemoryRecallMemory.From(new MemoryRecallHit(
            memory,
            MatchedVia: new[] { "porter" },
            PorterScore: 0.9,
            TrigramScore: 0.0));

        var json = JsonSerializer.Serialize(dto);

        Assert.Contains("\"matchedVia\":[\"porter\"]", json);
        Assert.Contains("\"porterScore\":0.9", json);
        Assert.Contains("\"trigramScore\":0", json);
    }

    [Fact]
    public void Serialize_MemoryRecallResponse_RoundTripsRecallMemories()
    {
        var memory = new Memory(MemoryType.Lesson, "Recall response content", tags: new[] { "workflow" });
        var response = new MemoryRecallResponse
        {
            RecallMemories = new[]
            {
                MemoryRecallMemory.From(new MemoryRecallHit(
                    memory,
                    MatchedVia: new[] { "porter", "trigram" },
                    PorterScore: 1.5,
                    TrigramScore: 0.3)),
            },
        };

        var options = new JsonSerializerOptions();
        options.Converters.Add(new Eling.Backend.Converters.MemoryIdJsonConverter());
        var json = JsonSerializer.Serialize(response, options);
        var deserialized = JsonSerializer.Deserialize<MemoryRecallResponse>(json, options);

        Assert.NotNull(deserialized);
        var hit = Assert.Single(deserialized!.RecallMemories);
        Assert.Equal(memory.Id, hit.Id);
        Assert.Equal(new[] { "porter", "trigram" }, hit.MatchedVia);
        Assert.Equal(1.5, hit.PorterScore);
        Assert.Equal(0.3, hit.TrigramScore);
    }

    [Fact]
    public void From_MemoryRecallHit_CarriesQueryMode()
    {
        var memory = new Memory(MemoryType.Fact, "Test content");
        var hit = new MemoryRecallHit(
            memory,
            MatchedVia: new[] { "porter", "trigram" },
            PorterScore: 1.2,
            TrigramScore: 0.8,
            QueryMode: "or-fallback");

        var dto = MemoryRecallMemory.From(hit);

        Assert.Equal("or-fallback", dto.QueryMode);
    }

    [Fact]
    public void From_PlainMemory_LeavesQueryModeNull()
    {
        var memory = new Memory(MemoryType.Fact, "Test content");

        var dto = MemoryRecallMemory.From(memory);

        Assert.Null(dto.QueryMode);
    }

    [Fact]
    public void Serialize_MemoryRecallMemory_IncludesQueryModeJsonProperty()
    {
        var memory = new Memory(MemoryType.Fact, "Test content");
        var dto = MemoryRecallMemory.From(new MemoryRecallHit(
            memory,
            MatchedVia: new[] { "porter" },
            PorterScore: 0.9,
            TrigramScore: 0.0,
            QueryMode: "or-fallback"));

        var json = JsonSerializer.Serialize(dto);

        Assert.Contains("\"queryMode\":\"or-fallback\"", json);
    }
}
