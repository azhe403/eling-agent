using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using Eling.Desktop.Services;
using Eling.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eling.Desktop.Tests;

/// <summary>
/// A turn in workspace A must survive switching to workspace B: the streaming
/// session belongs to its workspace, not to whichever chat happens to be current.
/// </summary>
public class ChatWorkspaceSwitchTests
{
    private const string WorkspaceA = @"C:\a";
    private const string WorkspaceB = @"C:\b";

    private const string Sse =
        "event: started\n" +
        "data: {\"chatId\":\"cA\"}\n\n" +
        "event: delta\n" +
        "data: {\"delta\":\"hi\"}\n\n" +
        "event: done\n" +
        "data: {\"turn\":{\"chatId\":\"cA\",\"assistant\":\"hi\",\"toolCalls\":[]}}\n\n";

    [Fact]
    public async Task SwitchWorkspaceMidTurn_RestoresInFlightSessionOnReturn()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = CreateViewModel(entered, release);
        await vm.LoadAsync();

        vm.SwitchToWorkspace(WorkspaceA);
        vm.Input = "hello";
        var send = vm.SendCommand.Execute().FirstAsync().ToTask();
        await entered.Task;

        Assert.Single(vm.Messages);
        Assert.True(vm.IsBusy);

        vm.SwitchToWorkspace(WorkspaceB);
        Assert.Empty(vm.Messages);
        Assert.False(vm.IsBusy);

        vm.SwitchToWorkspace(WorkspaceA);
        Assert.Single(vm.Messages); // the in-flight turn's user row must come back

        release.SetResult(true);
        await send;

        Assert.Contains(vm.Messages, m => m.IsAssistant && m.Text == "hi");
        Assert.False(vm.IsBusy);
    }

    private static ChatViewModel CreateViewModel(
        TaskCompletionSource<bool> entered,
        TaskCompletionSource<bool> release)
    {
        var handler = new GatedHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/agent/workspaces/"))
            {
                return Json("""{"roots":["C:\\a","C:\\b"]}""");
            }

            if (path.Contains("/api/agent/chats/stream"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(Sse, Encoding.UTF8, "text/event-stream")
                };
            }

            if (path.Contains("/api/agent/chats/"))
            {
                return Json("""[{"id":"cA","workspace":"C:\\a","title":"old","updatedAt":"2026-11-24T00:00:00+00:00"}]""");
            }

            return Json("""{"baseUrl":null,"model":null,"hasKey":false,"modelsCached":[]}""");
        }, entered, release);

        return new ChatViewModel(
            new ElingApiClient(() => "http://127.0.0.1:1", NullLogger<ElingApiClient>.Instance)
            {
                HttpClientFactory = () => new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:1/") }
            },
            NullLogger<ChatViewModel>.Instance,
            action =>
            {
                action();
                return true;
            });
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body)
    };

    private sealed class GatedHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        TaskCompletionSource<bool> entered,
        TaskCompletionSource<bool> release) : HttpMessageHandler
    {
        private int _streamCalls;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.Contains("/api/agent/chats/stream") &&
                Interlocked.Increment(ref _streamCalls) == 1)
            {
                entered.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
            }

            return respond(request);
        }
    }
}
