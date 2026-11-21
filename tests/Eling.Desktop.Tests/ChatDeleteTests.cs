using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Eling.Desktop.Services;
using Eling.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eling.Desktop.Tests;

public class ChatDeleteTests
{
    private int _deleteCalls;
    private int _chatListCalls;

    [Fact]
    public async Task PromptThenConfirm_DeletesChat_RefreshesSidebar()
    {
        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/agent/workspaces/"))
            {
                return Json("""{"roots":["C:\\work"]}""");
            }

            if (request.Method == HttpMethod.Delete && path.EndsWith("/api/agent/chats/c1"))
            {
                _deleteCalls++;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (path.Contains("/api/agent/chats/"))
            {
                _chatListCalls++;
                return Json(_chatListCalls == 1
                    ? """[{"id":"c1","workspace":"C:\\work","title":"Old chat","updatedAt":"2026-11-19T00:00:00+00:00"}]"""
                    : "[]");
            }

            return Json("""{"baseUrl":null,"model":null,"hasKey":false,"modelsCached":[]}""");
        });
        var vm = CreateViewModel(handler);
        await vm.LoadAsync();
        var chatRow = vm.SidebarItems.Single(r => r.ChatId == "c1");

        await vm.PromptDeleteChatCommand.Execute(chatRow).FirstAsync().ToTask();
        Assert.True(vm.IsDeletePromptOpen);
        Assert.Contains("Old chat", vm.DeletePromptText);

        await vm.ConfirmDeleteChatCommand.Execute().FirstAsync().ToTask();

        Assert.Equal(1, _deleteCalls);
        Assert.False(vm.IsDeletePromptOpen);
        Assert.DoesNotContain(vm.SidebarItems, r => r.ChatId == "c1");
        Assert.Equal("Chat deleted.", vm.StatusText);
    }

    [Fact]
    public async Task ConfirmDelete_OnOpenChat_ResetsToFreshChat()
    {
        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/agent/workspaces/"))
            {
                return Json("""{"roots":["C:\\work"]}""");
            }

            if (request.Method == HttpMethod.Delete && path.EndsWith("/api/agent/chats/c1"))
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (path.EndsWith("/api/agent/chats/c1"))
            {
                return Json("""{"code":"ok","message":"ok","data":[{"role":"user","text":"hi","toolName":null}]}""");
            }

            if (path.Contains("/api/agent/chats/"))
            {
                return Json("""[{"id":"c1","workspace":"C:\\work","title":"Open chat","updatedAt":"2026-11-19T00:00:00+00:00"}]""");
            }

            return Json("""{"baseUrl":null,"model":null,"hasKey":false,"modelsCached":[]}""");
        });
        var vm = CreateViewModel(handler);
        await vm.LoadAsync();
        vm.SelectedRow = vm.SidebarItems.Single(r => r.ChatId == "c1");
        for (var i = 0; i < 100 && vm.Messages.Count == 0; i++)
        {
            await Task.Delay(20);
        }

        Assert.Single(vm.Messages);

        await vm.PromptDeleteChatCommand.Execute(vm.SidebarItems.Single(r => r.ChatId == "c1")).FirstAsync().ToTask();
        await vm.ConfirmDeleteChatCommand.Execute().FirstAsync().ToTask();

        Assert.Empty(vm.Messages);
    }

    [Fact]
    public async Task CancelDelete_KeepsChat()
    {
        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/agent/workspaces/"))
            {
                return Json("""{"roots":["C:\\work"]}""");
            }

            if (request.Method == HttpMethod.Delete)
            {
                throw new InvalidOperationException("Delete must not be called after Cancel");
            }

            if (path.Contains("/api/agent/chats/"))
            {
                return Json("""[{"id":"c1","workspace":"C:\\work","title":"Keep me","updatedAt":"2026-11-19T00:00:00+00:00"}]""");
            }

            return Json("""{"baseUrl":null,"model":null,"hasKey":false,"modelsCached":[]}""");
        });
        var vm = CreateViewModel(handler);
        await vm.LoadAsync();
        var chatRow = vm.SidebarItems.Single(r => r.ChatId == "c1");

        await vm.PromptDeleteChatCommand.Execute(chatRow).FirstAsync().ToTask();
        Assert.True(vm.IsDeletePromptOpen);

        await vm.CancelDeleteChatCommand.Execute().FirstAsync().ToTask();

        Assert.False(vm.IsDeletePromptOpen);
        Assert.Contains(vm.SidebarItems, r => r.ChatId == "c1");
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

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
