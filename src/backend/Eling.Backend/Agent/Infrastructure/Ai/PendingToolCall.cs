namespace Eling.Backend.Agent.Infrastructure.Ai;

/// <summary>One in-flight tool call assembled from streamed deltas.</summary>
internal sealed record PendingToolCall(
    string Name,
    System.Text.StringBuilder Args);
