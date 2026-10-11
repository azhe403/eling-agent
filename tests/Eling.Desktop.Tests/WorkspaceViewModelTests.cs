using System.Net;
using System.Net.Http;
using System.Reactive.Threading.Tasks;
using Eling.Desktop.Models;
using Eling.Desktop.Services;
using Eling.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eling.Desktop.Tests;

/// <summary>
/// ViewModel-only regression tests for the workspace fixes (delete-per-row,
/// workspace switch reset, host browse navigation). No XAML, no window, no UI
/// thread — safe to run locally and in CI.
/// </summary>
public class WorkspaceViewModelTests
{
    private static ElingApiClient CreateClient(HttpMessageHandler handler)
    {
        return new ElingApiClient(() => "http://127.0.0.1:1", NullLogger<ElingApiClient>.Instance)
        {
            HttpClientFactory = () => new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:1/") }
        };
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body)
    };

    private static string RootsJson(IEnumerable<string> roots)
        => """{"roots":[""" + string.Join(",", roots.Select(r => "\"" + r.Replace("\\", "\\\\") + "\"")) + "]}";

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    private static (FoldersViewModel Vm, List<string> Roots, List<string> Removed) CreateFoldersVm()
    {
        var roots = new List<string> { @"C:\a", @"C:\b" };
        var removed = new List<string>();
        var handler = new StubHandler(request =>
        {
            var uri = request.RequestUri!;
            var path = uri.AbsolutePath;
            if (path.EndsWith("/api/agent/workspaces/"))
            {
                if (request.Method == HttpMethod.Delete)
                {
                    var query = uri.Query;
                    var marker = "path=";
                    var idx = query.IndexOf(marker, StringComparison.Ordinal);
                    if (idx >= 0)
                    {
                        var encoded = query[(idx + marker.Length)..];
                        var amp = encoded.IndexOf('&');
                        if (amp >= 0) encoded = encoded[..amp];
                        var decoded = Uri.UnescapeDataString(encoded);
                        removed.Add(decoded);
                        roots.RemoveAll(r => string.Equals(r, decoded, StringComparison.OrdinalIgnoreCase));
                    }

                    return Json("""{}""");
                }

                return Json(RootsJson(roots));
            }

            if (path.EndsWith("/api/agent/files/list"))
            {
                return Json("""{"entries":[]}""");
            }

            return Json("""{}""");
        });

        var vm = new FoldersViewModel(CreateClient(handler), NullLogger<FoldersViewModel>.Instance)
        {
            ApiLoadRetryAttempts = 1,
            ApiLoadRetryDelay = TimeSpan.FromMilliseconds(1)
        };
        return (vm, roots, removed);
    }

    [Fact]
    public async Task RemoveRoot_WithExplicitTarget_RemovesThatRoot_NotSelected()
    {
        var (vm, _, removed) = CreateFoldersVm();
        await vm.LoadAsync();
        Assert.Equal(@"C:\a", vm.SelectedRoot);

        await vm.RemoveRootCommand.Execute(@"C:\b").ToTask();

        Assert.Single(removed, @"C:\b");
        Assert.DoesNotContain(@"C:\b", vm.Roots);
        Assert.Contains(@"C:\a", vm.Roots);
        Assert.Equal(@"C:\a", vm.SelectedRoot);
    }

    [Fact]
    public async Task RemoveRoot_WithNullTarget_FallsBackToSelectedRoot()
    {
        var (vm, _, removed) = CreateFoldersVm();
        await vm.LoadAsync();

        await vm.RemoveRootCommand.Execute(null).ToTask();

        Assert.Single(removed, @"C:\a");
        Assert.DoesNotContain(@"C:\a", vm.Roots);
    }

    [Fact]
    public async Task SwitchWorkspace_ResetsCurrentPath_AndListsNewRoot()
    {
        var (vm, _, _) = CreateFoldersVm();
        await vm.LoadAsync();
        vm.CurrentPath = "sub/dir";

        await vm.SwitchWorkspaceAsync(@"C:\b");

        Assert.Equal(@"C:\b", vm.SelectedRoot);
        Assert.Equal("", vm.CurrentPath);
        Assert.Null(vm.SelectedEntry);
    }

    [Fact]
    public async Task HostBrowse_OpenEntry_WithExplicitParam_NavigatesEvenWhenSelectedDiffers()
    {
        var navigated = new List<string>();
        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/agent/host/drives"))
            {
                return Json("""["C:\\"]""");
            }

            if (path.EndsWith("/api/agent/host/browse"))
            {
                var query = request.RequestUri!.Query;
                var marker = "path=";
                var idx = query.IndexOf(marker, StringComparison.Ordinal);
                var target = idx >= 0 ? Uri.UnescapeDataString(query[(idx + marker.Length)..]) : "";
                navigated.Add(target);
                var escaped = target.Replace("\\", "\\\\");
                return Json("{\"path\":\"" + escaped + "\",\"parent\":\"C:\\\\\",\"entries\":[]}");
            }

            return Json("""{}""");
        });
        var vm = new HostBrowseViewModel(CreateClient(handler), NullLogger<HostBrowseViewModel>.Instance)
        {
            SelectedEntry = new HostEntry("other", @"C:\other", true)
        };

        await vm.OpenEntryAsync(new HostEntry("work", @"C:\work", true));

        Assert.Contains(@"C:\work", navigated);
        Assert.Equal(@"C:\work", vm.CurrentPath);
    }

    [Fact]
    public async Task HostBrowse_OpenEntry_NullFallsBackToSelected()
    {
        var navigated = new List<string>();
        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/agent/host/browse"))
            {
                var query = request.RequestUri!.Query;
                var marker = "path=";
                var idx = query.IndexOf(marker, StringComparison.Ordinal);
                var target = idx >= 0 ? Uri.UnescapeDataString(query[(idx + marker.Length)..]) : "";
                navigated.Add(target);
                var escaped = target.Replace("\\", "\\\\");
                return Json("{\"path\":\"" + escaped + "\",\"parent\":\"C:\\\\\",\"entries\":[]}");
            }

            return Json("""{}""");
        });
        var vm = new HostBrowseViewModel(CreateClient(handler), NullLogger<HostBrowseViewModel>.Instance)
        {
            SelectedEntry = new HostEntry("work", @"C:\work", true)
        };

        await vm.OpenEntryAsync(null);

        Assert.Contains(@"C:\work", navigated);
    }

    [Fact]
    public async Task HostBrowse_OpenEntry_FileDoesNotNavigate()
    {
        var navigated = new List<string>();
        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/agent/host/browse"))
            {
                navigated.Add("called");
                return Json("""{"path":"C:\\work","parent":"C:\\","entries":[]}""");
            }

            return Json("""{}""");
        });
        var vm = new HostBrowseViewModel(CreateClient(handler), NullLogger<HostBrowseViewModel>.Instance);

        await vm.OpenEntryAsync(new HostEntry("file.txt", @"C:\work\file.txt", false));

        Assert.Empty(navigated);
        Assert.Equal("", vm.CurrentPath);
    }
}
