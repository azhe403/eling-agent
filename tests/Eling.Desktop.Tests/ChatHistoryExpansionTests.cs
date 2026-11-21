using System.Linq;
using System.Net;
using System.Net.Http;
using Eling.Desktop.Services;
using Eling.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

public class ChatHistoryExpansionTests
{
    [Fact]
    public async Task HistoryLoad_ExpandsUserAndAssistant_KeepsToolCollapsed()
    {
        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/agent/workspaces/"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"roots":["C:\\work"]}""")
                };
            }

            if (path.EndsWith("/api/agent/chats/c1"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"code":"ok","message":"ok","data":[{"role":"user","text":"hello","toolName":null},{"role":"assistant","text":"hi there","toolName":null},{"role":"tool","text":"{\"big\":1}","toolName":"recall"}]}""")
                };
            }

            if (path.Contains("/api/agent/chats/"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"code":"ok","message":"ok","data":[{"id":"c1","workspace":"C:\\work","title":"Chat 1","updatedAt":"2026-11-19T00:00:00+00:00"}]}""")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"code":"ok","message":"ok","data":{"baseUrl":null,"model":null,"hasKey":false,"modelsCached":[]}}""")
            };
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
        var chatRow = vm.SidebarItems.First(r => r.ChatId == "c1");
        vm.SelectedRow = chatRow;

        for (var i = 0; i < 100 && vm.Messages.Count < 3; i++)
        {
            await Task.Delay(20);
        }

        Assert.Equal(3, vm.Messages.Count);
        Assert.True(vm.Messages[0].IsExpanded);
        Assert.True(vm.Messages[1].IsExpanded);
        Assert.False(vm.Messages[2].IsExpanded);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
