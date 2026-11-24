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
/// Starting a new chat must show a row in the sidebar immediately — before the
/// backend answers the stream request — using the first message as its title.
/// </summary>
public class ChatNewChatSidebarTests
{
    private const string Workspace = @"C:\a";

    [Fact]
    public async Task SendFirstMessage_ChatRowAppearsBeforeBackendResponds()
    {
        var streamRequested = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var handler = new StubHandler(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/agent/workspaces/"))
            {
                return Json("""{"roots":["C:\\a"]}""");
            }

            if (path.Contains("/api/agent/chats/stream"))
            {
                streamRequested.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "event: started\ndata: {\"chatId\":\"cA\"}\n\n" +
                        "event: delta\ndata: {\"delta\":\"hi\"}\n\n" +
                        "event: done\ndata: {\"turn\":{\"chatId\":\"cA\",\"assistant\":\"hi\",\"toolCalls\":[]}}\n\n",
                        Encoding.UTF8, "text/event-stream")
                };
            }

            if (path.Contains("/api/agent/chats/"))
            {
                // Backend has no saved chat yet while the stream is gated.
                return Json("[]");
            }

            return Json("""{"baseUrl":null,"model":null,"hasKey":false,"modelsCached":[]}""");
        });

        var vm = new ChatViewModel(
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

        await vm.LoadAsync();
        vm.SwitchToWorkspace(Workspace);
        Assert.DoesNotContain(vm.SidebarItems, r => r.IsChat);

        vm.Input = "hello";
        var send = vm.SendCommand.Execute().FirstAsync().ToTask();
        await streamRequested.Task;

        // No backend response yet, but the new chat must already be visible.
        var pending = vm.SidebarItems.FirstOrDefault(r => r.IsChat && string.Equals(r.DisplayName, "hello", StringComparison.Ordinal));
        Assert.NotNull(pending);
        Assert.Null(pending.ChatId);

        release.SetResult(true);
        await send;
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body)
    };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request);
    }
}
