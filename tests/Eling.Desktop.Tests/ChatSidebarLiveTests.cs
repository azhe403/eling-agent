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
/// The chat row must appear in the sidebar as soon as the turn starts. The backend
/// persists the chat at the beginning of RunTurnAsync (before emitting "started"),
/// so the desktop must refresh then — not only when the stream finishes.
/// </summary>
public class ChatSidebarLiveTests
{
    private const string Workspace = @"C:\a";

    [Fact]
    public async Task StreamStarted_RefreshesSidebar_ChatRowAppearsBeforeStreamEnds()
    {
        var streamRequested = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedWritten = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStream = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var chatListCalls = 0;

        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/agent/workspaces/"))
            {
                return Json("""{"roots":["C:\\a"]}""");
            }

            if (path.Contains("/api/agent/chats/stream"))
            {
                streamRequested.TrySetResult(true);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new GatedSseStream(startedWritten, releaseStream))
                };
            }

            if (path.Contains("/api/agent/chats/"))
            {
                chatListCalls++;
                // The backend saved the chat as soon as the turn began.
                var rows = startedWritten.Task.IsCompleted
                    ? """[{"id":"cA","workspace":"C:\\a","title":"hello","updatedAt":"2026-11-24T00:00:00+00:00"}]"""
                    : "[]";
                return Json(rows);
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
        vm.Input = "hello";
        var send = vm.SendCommand.Execute().FirstAsync().ToTask();
        await streamRequested.Task;

        // The backend has written the "started" frame; the chat exists on disk.
        await startedWritten.Task;
        var appeared = await WaitUntil(() => vm.SidebarItems.Any(r => r.ChatId == "cA"), TimeSpan.FromSeconds(2));
        Assert.True(appeared, "the new chat row must appear in the sidebar while the turn is still running");

        releaseStream.SetResult(true);
        await send;

        Assert.Contains(vm.SidebarItems, r => r.ChatId == "cA");
        Assert.True(chatListCalls >= 2, "sidebar must have been refreshed after the turn started");
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return condition();
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body)
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    /// <summary>
    /// Emits the "started" frame immediately so the reader can process it, then
    /// blocks until released before the rest. StreamContent avoids HttpClient
    /// buffering the whole body before the reader sees the first line.
    /// </summary>
    private sealed class GatedSseStream(
        TaskCompletionSource<bool> startedWritten,
        TaskCompletionSource<bool> release) : MemoryStream
    {
        private bool _started;
        private bool _finished;

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_started)
            {
                _started = true;
                var first = Encoding.UTF8.GetBytes("event: started\ndata: {\"chatId\":\"cA\"}\n\n");
                Write(first, 0, first.Length);
                Position = 0;
                startedWritten.TrySetResult(true);
            }
            else if (!_finished && Position >= Length)
            {
                await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                _finished = true;
                var rest = Encoding.UTF8.GetBytes(
                    "event: delta\ndata: {\"delta\":\"hi\"}\n\n" +
                    "event: done\ndata: {\"turn\":{\"chatId\":\"cA\",\"assistant\":\"hi\",\"toolCalls\":[]}}\n\n");
                var pos = Position;
                Position = Length;
                Write(rest, 0, rest.Length);
                Position = pos;
            }

            return await base.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
    }
}
