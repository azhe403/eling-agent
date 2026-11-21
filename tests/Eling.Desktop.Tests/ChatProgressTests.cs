using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using Eling.Desktop.Models;
using Eling.Desktop.Services;
using Eling.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eling.Desktop.Tests;

/// <summary>
/// Covers the in-flight liveness of the chat panel: a thinking indicator while a
/// turn is running, the auto-collapse of earlier tool rows when newer ones land,
/// and right-aligned user bubbles.
/// </summary>
public class ChatProgressTests
{
    private const string SseTwoTools =
        "event: started\n" +
        "data: {\"chatId\":\"c1\"}\n\n" +
        "event: tool\n" +
        "data: {\"name\":\"file_read\",\"arguments\":\"{\\\"path\\\":\\\"a.txt\\\"}\"}\n\n" +
        "event: tool_result\n" +
        "data: {\"name\":\"file_read\",\"output\":\"alpha\"}\n\n" +
        "event: tool\n" +
        "data: {\"name\":\"file_read\",\"arguments\":\"{\\\"path\\\":\\\"b.txt\\\"}\"}\n\n" +
        "event: tool_result\n" +
        "data: {\"name\":\"file_read\",\"output\":\"beta\"}\n\n" +
        "event: delta\n" +
        "data: {\"delta\":\"done\"}\n\n" +
        "event: done\n" +
        "data: {\"turn\":{\"chatId\":\"c1\",\"assistant\":\"done\",\"toolCalls\":[]}}\n\n";

    private const string SseOneDelta =
        "event: started\n" +
        "data: {\"chatId\":\"c1\"}\n\n" +
        "event: delta\n" +
        "data: {\"delta\":\"hi\"}\n\n" +
        "event: done\n" +
        "data: {\"turn\":{\"chatId\":\"c1\",\"assistant\":\"hi\",\"toolCalls\":[]}}\n\n";

    private static ElingApiClient CreateClient(HttpMessageHandler handler) =>
        new(() => "http://127.0.0.1:1", NullLogger<ElingApiClient>.Instance)
        {
            HttpClientFactory = () => new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:1/") }
        };

    private static ChatViewModel CreateViewModel(HttpMessageHandler handler) =>
        new(CreateClient(handler), NullLogger<ChatViewModel>.Instance, action =>
        {
            action();
            return true;
        })
        {
            ActiveWorkspace = @"C:\work"
        };

    private static HttpResponseMessage SseResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
    };

    [Fact]
    public async Task SendAsync_ShowsBusyAndWaitingStatus_WhileInFlight_ThenClearsIt()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new GatedHandler(entered, release, () => SseResponse(SseOneDelta));
        var vm = CreateViewModel(handler);
        vm.Input = "hello";

        var send = vm.SendCommand.Execute().FirstAsync().ToTask();
        await entered.Task;

        Assert.True(vm.IsBusy);
        Assert.Contains("Menunggu model", vm.StatusText);

        release.SetResult(true);
        await send;

        Assert.False(vm.IsBusy);
        Assert.Contains(vm.Messages, m => m.IsAssistant && m.Text == "hi");
    }

    [Fact]
    public async Task ToolResultRows_OnlyNewestStaysExpanded()
    {
        var handler = new StubHandler(_ => SseResponse(SseTwoTools));
        var vm = CreateViewModel(handler);
        vm.Input = "go";

        await vm.SendCommand.Execute().FirstAsync().ToTask();

        var toolRows = vm.Messages.Where(m => m.IsTool).ToList();
        Assert.Equal(2, toolRows.Count);
        Assert.False(toolRows[0].IsExpanded);
        Assert.True(toolRows[1].IsExpanded);
        Assert.Equal("Selesai — 2 tool call(s)", vm.StatusText);
    }

    [Fact]
    public void UserRow_IsRightAlignedBubble_CappedWidth()
    {
        var user = new ChatMessageRow("User", "hi", null);
        var assistant = new ChatMessageRow("Assistant", "hi", null);

        Assert.Equal(Avalonia.Layout.HorizontalAlignment.Right, user.RowHorizontalAlignment);
        Assert.Equal(560, user.RowMaxWidth);
        Assert.Equal(180, user.RowMinWidth);
        Assert.Equal(Avalonia.Layout.HorizontalAlignment.Stretch, assistant.RowHorizontalAlignment);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    private sealed class GatedHandler(
        TaskCompletionSource<bool> entered,
        TaskCompletionSource<bool> release,
        Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            entered.TrySetResult(true);
            await release.Task.ConfigureAwait(false);
            return respond();
        }
    }
}
