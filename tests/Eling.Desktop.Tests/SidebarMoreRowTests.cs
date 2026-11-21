using System.Linq;
using System.Net;
using System.Net.Http;
using Eling.Desktop.Services;
using Eling.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

public class SidebarMoreRowTests
{
    private const string Root = @"C:\work\eling-agent";

    private static ElingApiClient CreateClient(HttpMessageHandler handler) =>
        new(() => "http://127.0.0.1:1", NullLogger<ElingApiClient>.Instance)
        {
            HttpClientFactory = () => new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:1/") }
        };

    private static string SevenChats()
    {
        var chats = Enumerable.Range(1, 7)
            .Select(i => $$"""{"id":"c{{i}}","workspace":"C:\\work\\eling-agent","title":"Chat {{i}}","updatedAt":"2026-11-19T00:00:00+00:00"}""")
            .ToList();
        return "[" + string.Join(",", chats) + "]";
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body)
    };

    [Fact]
    public async Task LoadAsync_CapsChatsAtFive_WithMoreRow_ThenExpandsAndCollapses()
    {
        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/agent/workspaces/"))
            {
                return Json("""{"roots":["C:\\work\\eling-agent"]}""");
            }
            if (path.Contains("/api/agent/chats/"))
            {
                return Json(SevenChats());
            }
            return Json("""{"baseUrl":null,"model":null,"hasKey":false,"modelsCached":[]}""");
        });
        var vm = new ChatViewModel(CreateClient(handler), NullLogger<ChatViewModel>.Instance, action =>
        {
            action();
            return true;
        });

        await vm.LoadAsync();

        var chats = vm.SidebarItems.Where(r => r.IsChat).ToList();
        Assert.Equal(5, chats.Count);
        var more = vm.SidebarItems.Single(r => r.IsMoreRow);
        Assert.Equal("+ 2 more…", more.DisplayName);

        vm.SelectedRow = more;

        chats = vm.SidebarItems.Where(r => r.IsChat).ToList();
        Assert.Equal(7, chats.Count);
        Assert.Equal("Show less", vm.SidebarItems.Single(r => r.IsMoreRow).DisplayName);

        vm.SelectedRow = vm.SidebarItems.Single(r => r.IsMoreRow);

        chats = vm.SidebarItems.Where(r => r.IsChat).ToList();
        Assert.Equal(5, chats.Count);
        Assert.Equal("+ 2 more…", vm.SidebarItems.Single(r => r.IsMoreRow).DisplayName);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
