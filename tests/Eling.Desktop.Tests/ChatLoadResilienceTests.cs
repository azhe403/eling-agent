using System.Net;
using System.Net.Http;
using Eling.Desktop.Services;
using Eling.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eling.Desktop.Tests;

/// Covers the startup race that left the chat list empty. The view loads before
/// the backend it depends on has finished booting, and a failed load used to be
/// indistinguishable from a workspace that genuinely has nothing in it, so the
/// sidebar stayed blank until the user navigated somewhere and back.
public class ChatLoadResilienceTests
{
    private static ElingApiClient CreateClient(HttpMessageHandler handler)
    {
        return new ElingApiClient(() => "http://127.0.0.1:1", NullLogger<ElingApiClient>.Instance)
        {
            HttpClientFactory = () => new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:1/") }
        };
    }

    private static ChatViewModel CreateViewModel(HttpMessageHandler handler, int attempts, TimeSpan delay)
    {
        return new ChatViewModel(CreateClient(handler), NullLogger<ChatViewModel>.Instance)
        {
            ApiLoadRetryAttempts = attempts,
            ApiLoadRetryDelay = delay
        };
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body)
    };

    [Fact]
    public async Task LoadAsync_RetriesWhileBackendIsUnreachable_ThenFillsTheSidebar()
    {
        var workspaceCalls = 0;
        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/agent/workspaces/"))
            {
                workspaceCalls++;
                if (workspaceCalls == 1)
                {
                    throw new HttpRequestException("connection refused");
                }

                return Json("""{"roots":["C:\\work"]}""");
            }

            if (path.EndsWith("/api/agent/chats/"))
            {
                return Json("""[{"id":"c1","workspace":"C:\\work","title":"First chat","updatedAt":"2026-09-30T00:00:00+00:00"}]""");
            }

            return Json("""{"baseUrl":null,"model":null,"hasKey":false,"modelsCached":[]}""");
        });
        var vm = CreateViewModel(handler, attempts: 6, delay: TimeSpan.FromMilliseconds(1));

        await vm.LoadAsync();

        Assert.Equal("C:\\work", vm.ActiveWorkspace);
        Assert.Contains(vm.SidebarItems, row => row.DisplayName == "First chat");
    }

    [Fact]
    public async Task LoadAsync_DoesNotRetry_WhenTheBackendAnswersWithNoWorkspaces()
    {
        var workspaceCalls = 0;
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/agent/workspaces/"))
            {
                workspaceCalls++;
                return Json("""{"roots":[]}""");
            }

            return Json("""{"baseUrl":null,"model":null,"hasKey":false,"modelsCached":[]}""");
        });
        var vm = CreateViewModel(handler, attempts: 6, delay: TimeSpan.FromMilliseconds(1));

        await vm.LoadAsync();

        // One call to choose the workspace and one to fill the sidebar, with no
        // retries in between: an empty list is a real answer from a backend that
        // is up, not a backend that has not finished booting.
        Assert.Equal(2, workspaceCalls);
    }

    [Fact]
    public async Task LoadAsync_ReportsFailure_WhenTheRetryBudgetRunsOut()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));
        var vm = CreateViewModel(handler, attempts: 3, delay: TimeSpan.FromMilliseconds(1));

        await vm.LoadAsync();

        Assert.Contains("Load failed", vm.StatusText);
        Assert.Empty(vm.Workspaces);
    }

    [Fact]
    public async Task FoldersViewModel_RetriesWhileBackendIsUnreachable_ThenListsRoots()
    {
        var calls = 0;
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/agent/workspaces/"))
            {
                calls++;
                if (calls == 1)
                {
                    throw new HttpRequestException("connection refused");
                }

                return Json("""{"roots":["C:\\work"]}""");
            }

            return Json("""{"entries":[]}""");
        });
        var vm = new FoldersViewModel(CreateClient(handler), NullLogger<FoldersViewModel>.Instance)
        {
            ApiLoadRetryAttempts = 6,
            ApiLoadRetryDelay = TimeSpan.FromMilliseconds(1)
        };

        await vm.LoadAsync();

        Assert.Contains("C:\\work", vm.Roots);
    }

    private sealed class StubHandler(System.Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
