using System.ClientModel;
using Eling.Backend.Agent.Ports;
using Eling.Backend.Agent.Services;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;

namespace Eling.Backend.Agent.Infrastructure.Ai;

public sealed class MeaiChatGateway(ProviderStore store, ILogger<MeaiChatGateway> logger) : IChatGateway
{
    private static readonly TimeSpan StreamFirstByteTimeout = TimeSpan.FromSeconds(20);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> StreamingBrokenByEndpoint = new(StringComparer.OrdinalIgnoreCase);
    public async Task<SingleShotResult> CompleteAsync(SingleShotRequest request, CancellationToken ct)
    {
        if (!store.TryGetConfig(out var baseUrl, out _, out var apiKey))
        {
            throw new InvalidOperationException("Provider is not configured.");
        }

        try
        {
            var client = new ChatClient(
                request.Model,
                new ApiKeyCredential(string.IsNullOrEmpty(apiKey) ? "no-key" : apiKey),
                new OpenAIClientOptions { Endpoint = new Uri(baseUrl.TrimEnd('/') + "/") });

            var messages = new List<ChatMessage>();
            foreach (var message in request.Messages)
            {
                messages.Add(message.Role switch
                {
                    AgentRole.System => new SystemChatMessage(message.Text),
                    AgentRole.User => new UserChatMessage(message.Text),
                    AgentRole.Tool => new ToolChatMessage(message.ToolCallId ?? string.Empty, message.Text),
                    _ => new AssistantChatMessage(message.Text)
                });
            }

            var options = new ChatCompletionOptions();
            foreach (var tool in request.Tools)
            {
                options.Tools.Add(ChatTool.CreateFunctionTool(
                    tool.Name,
                    tool.Description,
                    BinaryData.FromString(tool.ParametersJsonSchema)));
            }

            if (options.Tools.Count > 0)
            {
                options.ToolChoice = ChatToolChoice.CreateAutoChoice();
            }

            ClientResult<ChatCompletion> result = await client.CompleteChatAsync(messages, options, ct);
            var completion = result.Value;

            var text = string.Concat(completion.Content
                .Where(p => p.Kind == ChatMessageContentPartKind.Text)
                .Select(p => p.Text));

            var toolCalls = completion.ToolCalls
                .Select(t => new ToolCallRequest(t.Id, t.FunctionName, t.FunctionArguments.ToString()))
                .ToList();

            return new SingleShotResult(string.IsNullOrEmpty(text) ? null : text, toolCalls);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            logger.LogWarning(ex, "Agent chat completion failed");
            throw;
        }
    }

    public async IAsyncEnumerable<ChatStreamEvent> CompleteStreamingAsync(
        SingleShotRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (!store.TryGetConfig(out var baseUrl, out _, out var apiKey))
        {
            throw new InvalidOperationException("Provider is not configured.");
        }

        var endpointKey = baseUrl.TrimEnd('/').ToLowerInvariant();
        if (StreamingBrokenByEndpoint.ContainsKey(endpointKey))
        {
            throw new IOException("Streaming is disabled for this endpoint after a previous timeout; using single-shot fallback.");
        }

        var client = new ChatClient(
            request.Model,
            new ApiKeyCredential(string.IsNullOrEmpty(apiKey) ? "no-key" : apiKey),
            new OpenAIClientOptions { Endpoint = new Uri(baseUrl.TrimEnd('/') + "/") });

        var messages = new List<ChatMessage>();
        foreach (var message in request.Messages)
        {
            messages.Add(message.Role switch
            {
                AgentRole.System => new SystemChatMessage(message.Text),
                AgentRole.User => new UserChatMessage(message.Text),
                AgentRole.Tool => new ToolChatMessage(message.ToolCallId ?? string.Empty, message.Text),
                _ => new AssistantChatMessage(message.Text)
            });
        }

        var options = new ChatCompletionOptions();
        foreach (var tool in request.Tools)
        {
            options.Tools.Add(ChatTool.CreateFunctionTool(
                tool.Name,
                tool.Description,
                BinaryData.FromString(tool.ParametersJsonSchema)));
        }

        if (options.Tools.Count > 0)
        {
            options.ToolChoice = ChatToolChoice.CreateAutoChoice();
        }

var pending = new Dictionary<string, (string Name, System.Text.StringBuilder Args)>(StringComparer.Ordinal);
string? currentToolCallId = null;
using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(StreamFirstByteTimeout);
        var enumerator = client.CompleteChatStreamingAsync(messages, options, timeoutCts.Token).GetAsyncEnumerator();
        try
        {
            while (true)
            {
                bool moved;
                try
                {
                    moved = await enumerator.MoveNextAsync();
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    StreamingBrokenByEndpoint[endpointKey] = true;
                    logger.LogWarning("Provider stream produced no data within {Seconds}s; endpoint marked non-streaming", StreamFirstByteTimeout.TotalSeconds);
                    throw new IOException("Provider stream timed out waiting for data.");
                }

                if (!moved)
                {
                    break;
                }

                timeoutCts.CancelAfter(Timeout.InfiniteTimeSpan);
                var update = enumerator.Current;
                foreach (var part in update.ContentUpdate)
                {
                    if (part.Kind == ChatMessageContentPartKind.Text && !string.IsNullOrEmpty(part.Text))
                    {
                        yield return new ChatTextDelta(part.Text);
                    }
                }

foreach (var toolUpdate in update.ToolCallUpdates)
            {
                // OpenAI-compatible providers may omit ToolCallId on continuation
                // deltas (only the first chunk of a tool call carries it). The SDK
                // does not surface the delta index, so attribute id-less chunks to
                // the most recently opened tool call, or open a synthetic one.
                var id = toolUpdate.ToolCallId;
                if (string.IsNullOrEmpty(id))
                {
                    id = currentToolCallId ?? $"call_{pending.Count}";
                }
                else
                {
                    currentToolCallId = id;
                }

                if (!pending.TryGetValue(id, out var entry))
                {
                    entry = (toolUpdate.FunctionName ?? string.Empty, new System.Text.StringBuilder());
                    pending[id] = entry;
                }
                else if (!string.IsNullOrEmpty(toolUpdate.FunctionName) && string.IsNullOrEmpty(entry.Name))
                {
                    entry = (toolUpdate.FunctionName, entry.Args);
                    pending[id] = entry;
                }

                if (toolUpdate.FunctionArgumentsUpdate != null)
                {
                    entry.Args.Append(toolUpdate.FunctionArgumentsUpdate.ToString());
                }
            }
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }

        foreach (var (id, (name, args)) in pending)
        {
            yield return new ChatToolRequest(new ToolCallRequest(id, name, args.ToString()));
        }
    }
}
