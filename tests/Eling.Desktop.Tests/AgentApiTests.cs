using System.Net;
using System.Net.Http;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using Eling.Desktop.Services;
using Eling.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eling.Desktop.Tests;

public class AgentApiTests
{
    private static ElingApiClient CreateClient(HttpMessageHandler handler)
    {
        var client = new ElingApiClient(() => "http://127.0.0.1:1", NullLogger<ElingApiClient>.Instance)
        {
            HttpClientFactory = () => new HttpClient(handler) { BaseAddress = new System.Uri("http://127.0.0.1:1/") }
        };
        return client;
    }

    [Fact]
    public async Task GetProvider_ParsesHasKey()
    {
        var handler = new StubHandler(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"baseUrl":"http://127.0.0.1:11434/v1","model":"m1","hasKey":true,"modelsCached":["m1"]}""")
            });
        var client = CreateClient(handler);

        var provider = await client.GetProviderAsync();
        Assert.NotNull(provider);
        Assert.True(provider.HasKey);
        Assert.Equal("m1", provider.Model);
    }

    [Fact]
    public async Task UpdateProvider_EchoesView()
    {
        var handler = new StubHandler(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"baseUrl":"http://x/v1","model":"m2","hasKey":true,"modelsCached":[]}""")
            });
        var client = CreateClient(handler);

        var provider = await client.UpdateProviderAsync("http://x/v1", "m2", "k");
        Assert.NotNull(provider);
        Assert.Equal("m2", provider.Model);
    }

    [Fact]
    public async Task FetchModels_ParsesTwoIds()
    {
        var handler = new StubHandler(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"models":["m1","m2"]}""")
            });
        var client = CreateClient(handler);

        var models = await client.FetchModelsAsync();
        Assert.Equal(["m1", "m2"], models);
    }

    [Fact]
    public async Task WorkspaceFile_RoundTrips()
    {
        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/agent/workspaces/"))
            {
                var body = """{"roots":["C:\\work\\proj"]}""";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            }

            if (path.EndsWith("/api/agent/files/list"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"entries":[{"path":"a.txt","isDirectory":false}]}""")
                };
            }

            if (path.EndsWith("/api/agent/files/read"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"content":"hello","truncated":false}""")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = CreateClient(handler);

        var roots = await client.GetWorkspacesAsync();
        Assert.Single(roots);
        var entries = await client.ListDirAsync(roots[0], "");
        Assert.Single(entries);
        var file = await client.ReadFileAsync(roots[0], "a.txt");
        Assert.NotNull(file);
        Assert.Equal("hello", file.Content);
        Assert.True(await client.AddWorkspaceAsync(roots[0]));
        Assert.True(await client.RemoveWorkspaceAsync(roots[0]));
        Assert.True(await client.WriteFileAsync(roots[0], "a.txt", "hello"));
    }

    private sealed class StubHandler(System.Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    [Fact]
    public async Task ChatViewModel_LoadsModels_AndSwitchSaves()
    {
        var putHit = false;
        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/agent/provider/") && request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"baseUrl":"http://x/v1","model":"m1","hasKey":true,"modelsCached":["m1","m2"]}""")
                };
            }

            if (path.EndsWith("/api/agent/provider/") && request.Method == HttpMethod.Put)
            {
                putHit = true;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"baseUrl":"http://x/v1","model":"m2","hasKey":true,"modelsCached":["m1","m2"]}""")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"roots":[]}""")
            };
        });
        var client = CreateClient(handler);
        var vm = new ChatViewModel(client, NullLogger<ChatViewModel>.Instance);

        await vm.LoadAsync();
        Assert.Contains("m1", vm.Models);
        Assert.Contains("m2", vm.Models);
        Assert.Equal("m1", vm.SelectedModel);

        vm.SelectedModel = "m2";
        await Task.Delay(500);
        Assert.True(putHit);
    }

    [Fact]
    public async Task ChatViewModel_AutoFetchesModels_WhenCacheEmpty()
    {
        var fetchHit = false;
        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/agent/provider/") && request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"baseUrl":"http://x/v1","model":null,"hasKey":true,"modelsCached":[]}""")
                };
            }

            if (path.EndsWith("/api/agent/provider/models"))
            {
                fetchHit = true;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"models":["auto1","auto2"]}""")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"roots":[]}""")
            };
        });
        var client = CreateClient(handler);
        var vm = new ChatViewModel(client, NullLogger<ChatViewModel>.Instance);

        await vm.LoadAsync();
        Assert.True(fetchHit);
        Assert.Equal(["auto1", "auto2"], vm.Models);
    }

    [Fact]
    public async Task ChatViewModel_StreamsRowsIncrementally_InOrder()
    {
        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/agent/chats/stream"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "event: started\ndata: {\"chatId\":\"c1\"}\n\n" +
                        "event: delta\ndata: {\"delta\":\"he\"}\n\n" +
                        "event: delta\ndata: {\"delta\":\"llo\"}\n\n" +
                        "event: tool\ndata: {\"name\":\"read_file\",\"arguments\":\"{}\"}\n\n" +
                        "event: done\ndata: {\"chatId\":\"c1\",\"assistant\":\"hello\",\"toolCalls\":[]}\n\n")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"roots":[]}""")
            };
        });
        var client = CreateClient(handler);
        var vm = new ChatViewModel(client, NullLogger<ChatViewModel>.Instance);
        vm.ActiveWorkspace = "ws";
        vm.Input = "hello";

        await vm.SendCommand.Execute().ToTask();
        Assert.False(vm.IsBusy);
        Assert.Equal(3, vm.Messages.Count);
        Assert.Equal("User", vm.Messages[0].Role);
        Assert.Equal("Assistant", vm.Messages[1].Role);
        Assert.Equal("hello", vm.Messages[1].Text);
        Assert.Equal("Tool", vm.Messages[2].Role);
    }

    [Fact]
    public async Task HostBrowse_RoundTrips()
    {
        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/agent/host/drives"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""["C:\\"]""")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"path":"C:\\work","parent":"C:\\","entries":[{"path":"C:\\work\\a","isDirectory":true}]}""")
            };
        });
        var client = CreateClient(handler);

        var drives = await client.GetHostDrivesAsync();
        Assert.Single(drives);
        var browsed = await client.BrowseHostAsync(drives[0]);
        Assert.NotNull(browsed);
        Assert.Single(browsed.Entries);
    }
}
