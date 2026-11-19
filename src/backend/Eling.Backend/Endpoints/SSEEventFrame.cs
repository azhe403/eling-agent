namespace Eling.Backend.Endpoints;

/// <summary>One SSE frame: the event name and its JSON payload.</summary>
internal sealed record SSEEventFrame(
    string Kind,
    object Payload);
