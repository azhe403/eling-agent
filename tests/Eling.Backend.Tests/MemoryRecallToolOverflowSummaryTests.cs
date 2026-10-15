using Eling.Backend.Dtos;
using Eling.Backend.Mcp.Tools;
using Eling.Core.Memory;
using Eling.Core.MemoryRecall;

namespace Eling.Backend.Tests;

/// <summary>
/// The overflow summary contract on the tool surface: judge digest when the
/// summarizer answers, deterministic digest with a note when it does not,
/// nothing when there is no overflow or the caller opts out.
/// </summary>
public sealed class MemoryRecallToolOverflowSummaryTests : IDisposable
{
    private readonly string _root;

    public MemoryRecallToolOverflowSummaryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "eling-overflow-summary-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Recall_OverflowPresent_JudgeSummarySurfaces()
    {
        var summarizer = new StubSummarizer(new MemorySummary(["digest paragraph one", "digest paragraph two"], "judge"));
        var tool = new MemoryRecallTool(new OverflowRecallService(ResultWithOverflow(12)), cwd: _root, summarizer: summarizer);

        var response = await tool.RecallAsync(new MemoryRecallContextInput { Topics = ["any"] });

        Assert.NotNull(response.RecallSummary);
        Assert.Equal(12, response.RecallSummary!.ItemCount);
        Assert.Equal(10, response.RecallSummary.SummarizedCount);
        Assert.Equal("judge", response.RecallSummary.Source);
        Assert.Equal(2, response.RecallSummary.Paragraphs.Count);
        Assert.Equal(10, summarizer.LastItemCount);
        Assert.Null(response.RecallSummary.Note);
    }

    [Fact]
    public async Task Recall_SummarizerThrows_FallsBackToHeuristicWithNote()
    {
        var tool = new MemoryRecallTool(
            new OverflowRecallService(ResultWithOverflow(3)),
            cwd: _root,
            summarizer: new StubSummarizer(throwing: true));

        var response = await tool.RecallAsync(new MemoryRecallContextInput { Topics = ["any"] });

        Assert.Equal("heuristic", response.RecallSummary!.Source);
        Assert.Contains("judge-failed", response.RecallSummary.Note);
        Assert.NotEmpty(response.RecallSummary.Paragraphs);
    }

    [Fact]
    public async Task Recall_SummarizerReturnsNothing_HeuristicWithNote()
    {
        var tool = new MemoryRecallTool(
            new OverflowRecallService(ResultWithOverflow(3)),
            cwd: _root,
            summarizer: new StubSummarizer(new MemorySummary([], "judge")));

        var response = await tool.RecallAsync(new MemoryRecallContextInput { Topics = ["any"] });

        Assert.Equal("heuristic", response.RecallSummary!.Source);
        Assert.Contains("no usable summary", response.RecallSummary.Note);
    }

    [Fact]
    public async Task Recall_NoSummarizerConfigured_HeuristicWithNote()
    {
        var tool = new MemoryRecallTool(new OverflowRecallService(ResultWithOverflow(3)), cwd: _root);

        var response = await tool.RecallAsync(new MemoryRecallContextInput { Topics = ["any"] });

        Assert.Equal("heuristic", response.RecallSummary!.Source);
        Assert.Contains("judge not configured", response.RecallSummary.Note);
    }

    [Fact]
    public async Task Recall_SummarizeOverflowDisabled_NoSummary()
    {
        var tool = new MemoryRecallTool(
            new OverflowRecallService(ResultWithOverflow(3)),
            cwd: _root,
            summarizer: new StubSummarizer(new MemorySummary(["x"], "judge")));

        var response = await tool.RecallAsync(new MemoryRecallContextInput { Topics = ["any"] }, summarizeOverflow: false);

        Assert.Null(response.RecallSummary);
    }

    [Fact]
    public async Task Recall_NoOverflow_NoSummary()
    {
        var tool = new MemoryRecallTool(
            new OverflowRecallService(ResultWithOverflow(0)),
            cwd: _root,
            summarizer: new StubSummarizer(new MemorySummary(["x"], "judge")));

        var response = await tool.RecallAsync(new MemoryRecallContextInput { Topics = ["any"] });

        Assert.Null(response.RecallSummary);
    }

    private static MemoryRecallResult ResultWithOverflow(int overflowCount)
    {
        var overflow = Enumerable.Range(0, overflowCount)
            .Select(i => new MemoryRecallHit(new Memory(MemoryType.Fact, $"overflow fact {i}"), ["porter"], 1.0, 0.0, "or-fallback"))
            .ToList();
        return new MemoryRecallResult([], [], [], new MemoryRecallStats(0, 0, 0, 0, 0), null, overflow);
    }

    private sealed class OverflowRecallService(MemoryRecallResult result) : IMemoryRecallService
    {
        public Task<MemoryRecallResult> RecallAsync(
            MemoryRecallContext? context,
            int recallLimit = 10,
            int recentLimit = 10,
            string? scope = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(result);
    }

    private sealed class StubSummarizer : ISemanticSummarizer
    {
        private readonly MemorySummary? _result;
        private readonly bool _throwing;

        public StubSummarizer(MemorySummary? result = null, bool throwing = false)
        {
            _result = result;
            _throwing = throwing;
        }

        public int LastItemCount { get; private set; }

        public Task<MemorySummary> SummarizeAsync(
            IReadOnlyCollection<Memory> items,
            CancellationToken cancellationToken)
        {
            LastItemCount = items.Count;
            if (_throwing)
            {
                throw new InvalidOperationException("Semantic judge is not configured.");
            }

            return Task.FromResult(_result ?? new MemorySummary([], "judge"));
        }
    }
}
