using System.ClientModel;
using System.ClientModel.Primitives;
using Eling.Backend.Agent.Ports;
using Eling.Backend.Agent.Services;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;

namespace Eling.Backend.Agent.Infrastructure.Ai;

public sealed class MeaiChatGateway(ProviderStore store, ILogger<MeaiChatGateway> logger) : IChatGateway
{
    private static readonly TimeSpan StreamFirstByteTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The opencode gateway routes a request by session and answers
    /// <c>HTTP 400 (MissingSessionID:)</c> when this header is absent. It is
    /// hardcoded rather than a provider setting: the value is the chat's own id,
    /// which arrives on <see cref="SingleShotRequest.SessionId"/>, so there is
    /// nothing for a user to configure and nothing to get wrong.
    /// </summary>
    private const string SessionHeaderName = "x-opencode-session";

    /// <summary>
    /// Builds a chat client that labels every request with the calling chat's
    /// session id. The <see cref="HttpClient"/> is created per client, never
    /// cached or shared: the session id is a default header on it, so one shared
    /// instance would pin the first chat's id onto every later request.
    /// </summary>
    private static ChatClient CreateChatClient(SingleShotRequest request, string baseUrl, string? apiKey)
    {
        var httpClient = new HttpClient();
        if (!string.IsNullOrWhiteSpace(request.SessionId))
        {
            httpClient.DefaultRequestHeaders.Add(SessionHeaderName, request.SessionId);
        }

        return new ChatClient(
            request.Model,
            new ApiKeyCredential(string.IsNullOrEmpty(apiKey) ? "no-key" : apiKey),
            new OpenAIClientOptions
            {
                Endpoint = new Uri(baseUrl.TrimEnd('/') + "/"),
                Transport = new HttpClientPipelineTransport(httpClient)
            });
    }

    /// <summary>
    /// Maps the stored turn history onto the wire format. An assistant message
    /// that called tools is replayed WITH those tool calls: the tool messages
    /// that follow are only valid as the answer to a declared tool_call, and
    /// opencode rejects an orphaned one with <c>invalid_request_error</c>.
    /// </summary>
    private static List<ChatMessage> BuildMessages(SingleShotRequest request)
    {
        var messages = new List<ChatMessage>();
        foreach (var message in request.Messages)
        {
            messages.Add(message.Role switch
            {
                AgentRole.System => new SystemChatMessage(message.Text),
                AgentRole.User => new UserChatMessage(message.Text),
                AgentRole.Tool => new ToolChatMessage(message.ToolCallId ?? string.Empty, message.Text),
                _ => BuildAssistantMessage(message)
            });
        }

        return messages;
    }

    private static AssistantChatMessage BuildAssistantMessage(AgentMessage message)
    {
        var assistant = new AssistantChatMessage(message.Text);
        foreach (var call in message.ToolCalls ?? [])
        {
            assistant.ToolCalls.Add(ChatToolCall.CreateFunctionToolCall(
                call.Id,
                call.Name,
                BinaryData.FromString(call.ArgumentsJson)));
        }

        return assistant;
    }

    /// <summary>
    /// Builds the tool block. Kept beside <see cref="BuildMessages"/> because a
    /// tool definition and the assistant message that calls it must never drift
    /// apart — the provider rejects a call to a tool it was not offered.
    /// </summary>
    private static ChatCompletionOptions BuildOptions(SingleShotRequest request)
    {
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

        return options;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> StreamingBrokenByEndpoint = new(StringComparer.OrdinalIgnoreCase);
    public async Task<SingleShotResult> CompleteAsync(SingleShotRequest request, CancellationToken ct)
    {
        if (!store.TryGetConfig(out var baseUrl, out _, out var apiKey))
        {
            throw new InvalidOperationException("Provider is not configured.");
        }

        try
        {
            var client = CreateChatClient(request, baseUrl, apiKey);
            var messages = BuildMessages(request);
            var options = BuildOptions(request);

            ClientResult<ChatCompletion> result = await client.CompleteChatAsync(messages, options, ct);
            var completion = result.Value;

            var text = string.Concat(completion.Content
                .Where(p => p.Kind == ChatMessageContentPartKind.Text)
                .Select(p => p.Text));

            var toolCalls = completion.ToolCalls
                .Select(t => new ToolCallRequest(t.Id, t.FunctionName, t.FunctionArguments.ToString()))
                .ToList();

            var finishReason = completion.FinishReason.ToString().ToLowerInvariant();

            return new SingleShotResult(string.IsNullOrEmpty(text) ? null : text, toolCalls, finishReason);
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

        var client = CreateChatClient(request, baseUrl, apiKey);
        var messages = BuildMessages(request);
        var options = BuildOptions(request);

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
                // Normalise through a string before lowercasing: the provider
                // finish reason is an optional enum whose ToString() is not
                // known to be non-null, and a blank reason carries no
                // information worth emitting.
                var streamFinish = update.FinishReason?.ToString();
                if (!string.IsNullOrEmpty(streamFinish))
                {
                    yield return new ChatFinishReasonEvent(streamFinish.ToLowerInvariant());
                }
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
