using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Eling.Desktop.Services;
using Eling.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eling.Desktop.Tests;

public class ChatRetryTests
{
    private const string SseOneDelta =
        "event: started\n" +
        "data: {\"chatId\":\"c1\"}\n\n" +
        "event: delta\n" +
        "data: {\"delta\":\"hi\"}\n\n" +
        "event: done\n" +
        "data: {\"turn\":{\"chatId\":\"c1\",\"assistant\":\"hi\",\"toolCalls\":[]}}\n\n";

    private const string SseFailed =
        "event: failed\n" +
        "data: {\"message\":\"oops\"}\n\n";

    [Fact]
    public async Task SendFails_ShowsRetry_RetryResendsAndSucceeds()
    {
        var streamCalls = 0;
        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/api/agent/chats/stream"))
            {
                streamCalls++;
                if (streamCalls == 1)
                {
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }
                return SseResponse(SseOneDelta);
            }

            return Json("""{"code":"ok","message":"ok","data":{"roots":["C:\\work"]}}""");
        });
        var vm = CreateViewModel(handler);
        vm.ActiveWorkspace = @"C:\work";
        vm.Input = "hello";

        await vm.SendCommand.Execute().FirstAsync().ToTask();

        Assert.True(vm.HasRetry);
        Assert.Contains("Send failed", vm.StatusText);
        var unanswered = vm.Messages.Count(m => m.IsUser);
        Assert.Equal(1, unanswered);
        Assert.DoesNotContain(vm.Messages, m => m.IsAssistant);

        await vm.RetryCommand.Execute().FirstAsync().ToTask();

        Assert.False(vm.HasRetry);
        Assert.Contains(vm.Messages, m => m.IsUser);
        Assert.Contains(vm.Messages, m => m.IsAssistant && m.Text == "hi");
        Assert.Equal(2, streamCalls);
    }

    [Fact]
    public async Task StreamFailed_ShowsRetry_RetryResendsAndSucceeds()
    {
        var streamCalls = 0;
        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/api/agent/chats/stream"))
            {
                streamCalls++;
                if (streamCalls == 1)
                {
                    return SseResponse(SseFailed);
                }
                return SseResponse(SseOneDelta);
            }

            return Json("""{"code":"ok","message":"ok","data":{"roots":["C:\\work"]}}""");
        });
        var vm = CreateViewModel(handler);
        vm.ActiveWorkspace = @"C:\work";
        vm.Input = "hello";

        await vm.SendCommand.Execute().FirstAsync().ToTask();

        Assert.True(vm.HasRetry);
        Assert.Contains("oops", vm.StatusText);
        Assert.DoesNotContain(vm.Messages, m => m.IsAssistant);

        await vm.RetryCommand.Execute().FirstAsync().ToTask();

        Assert.False(vm.HasRetry);
        Assert.Contains(vm.Messages, m => m.IsAssistant && m.Text == "hi");
    }

    private static ChatViewModel CreateViewModel(HttpMessageHandler handler) =>
        new(
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

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body)
    };

    private static HttpResponseMessage SseResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "text/event-stream")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
