namespace Eling.Backend.Agent.Ports;

public interface IChatGateway
{
    Task<SingleShotResult> CompleteAsync(SingleShotRequest request, CancellationToken ct);
    IAsyncEnumerable<ChatStreamEvent> CompleteStreamingAsync(SingleShotRequest request, CancellationToken ct);
}
