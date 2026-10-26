using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Eling.Desktop.Models;
using Microsoft.Extensions.Logging;

namespace Eling.Desktop.Services;

public sealed class ElingApiClient
{
    private readonly BackendSupervisor? _supervisor;
    private readonly Func<string> _baseUrlProvider;
    private readonly ILogger<ElingApiClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public Func<HttpClient>? HttpClientFactory { get; set; }

    public ElingApiClient(BackendSupervisor supervisor, ILogger<ElingApiClient> logger)
    {
        _supervisor = supervisor;
        _baseUrlProvider = () => supervisor.BaseUrl ?? "http://127.0.0.1:4417";
        _logger = logger;
    }

    public ElingApiClient(Func<string> baseUrlProvider, ILogger<ElingApiClient> logger)
    {
        _supervisor = null;
        _baseUrlProvider = baseUrlProvider;
        _logger = logger;
    }

    private HttpClient GetClient()
    {
        if (HttpClientFactory is not null)
        {
            return HttpClientFactory();
        }

        var baseUrl = _baseUrlProvider();
        return new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/")
        };
    }

    public async Task<MemoryListDto?> GetMemoriesAsync(
        bool allSelected = true,
        IReadOnlyList<string>? roots = null,
        bool globalIncluded = false,
        int? limit = null,
        string? type = null,
        string? q = null,
        string? tier = null,
        CancellationToken cancellationToken = default)
    {
        string url;
        var hasRoots = roots != null && roots.Count > 0;

        if (allSelected)
        {
            var parts = new List<string>();
            if (limit is not null) parts.Add($"limit={limit.Value}");
            if (!string.IsNullOrWhiteSpace(type) && !string.Equals(type, "All", StringComparison.OrdinalIgnoreCase)) parts.Add($"type={Uri.EscapeDataString(type)}");
            if (!string.IsNullOrWhiteSpace(q)) parts.Add($"q={Uri.EscapeDataString(q.Trim())}");
            url = $"api/aggregated/memories?{string.Join("&", parts)}";
        }
        else if (!hasRoots)
        {
            // Nothing ticked reads as the global scope whatever the global switch
            // says — the dashboard does the same, so an empty selection returns the
            // global list instead of the 400 the project endpoint gives for it.
            var parts = new List<string>();
            if (limit is not null) parts.Add($"limit={limit.Value}");
            if (!string.IsNullOrWhiteSpace(type) && !string.Equals(type, "All", StringComparison.OrdinalIgnoreCase)) parts.Add($"type={Uri.EscapeDataString(type)}");
            if (!string.IsNullOrWhiteSpace(q)) parts.Add($"q={Uri.EscapeDataString(q.Trim())}");
            url = $"api/global/memories?{string.Join("&", parts)}";
        }
        else
        {
            var parts = new List<string>();
            if (hasRoots)
            {
                foreach (var r in roots!) parts.Add($"project={Uri.EscapeDataString(r)}");
            }
            parts.Add("includeAncestors=true");
            if (limit is not null) parts.Add($"limit={limit.Value}");
            if (!string.IsNullOrWhiteSpace(type) && !string.Equals(type, "All", StringComparison.OrdinalIgnoreCase)) parts.Add($"type={Uri.EscapeDataString(type)}");
            if (!string.IsNullOrWhiteSpace(q)) parts.Add($"q={Uri.EscapeDataString(q.Trim())}");
            if (!string.IsNullOrWhiteSpace(tier) && !string.Equals(tier, "project", StringComparison.OrdinalIgnoreCase)) parts.Add($"tier={Uri.EscapeDataString(tier)}");
            if (globalIncluded) parts.Add("global=true");
            url = $"api/project/memories?{string.Join("&", parts)}";
        }

        try
        {
            using var client = GetClient();
            var response = await client.GetAsync(url, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<MemoryListDto>(JsonOptions, cancellationToken);
            }
            _logger.LogWarning("GetMemories failed with status {StatusCode}", response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching memories from {Url}", url);
        }

        return null;
    }

    public async Task<MemoryDto?> CreateMemoryAsync(string content, string type, List<string>? tags, string scope = "all", CancellationToken cancellationToken = default)
    {
        var url = scope switch
        {
            "global" => "api/global/memories",
            "all" => "api/memories",
            _ => $"api/project/memories?projectRoot={Uri.EscapeDataString(scope)}"
        };

        var payload = new { content, type, tags };
        try
        {
            using var client = GetClient();
            var response = await client.PostAsJsonAsync(url, payload, JsonOptions, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<MemoryDto>(JsonOptions, cancellationToken);
            }
            _logger.LogWarning("CreateMemory failed with status {StatusCode}", response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating memory at {Url}", url);
        }

        return null;
    }

    public async Task<MemoryDto?> UpdateMemoryAsync(string id, string? content, string? type, string? status, string scope = "all", string? projectRoot = null, string? tier = null, IReadOnlyList<string>? tags = null, CancellationToken cancellationToken = default)
    {
        string url;
        if (string.Equals(scope, "global", StringComparison.OrdinalIgnoreCase))
        {
            url = $"api/global/memories/{id}";
        }
        else
        {
            var root = !string.IsNullOrWhiteSpace(projectRoot) ? projectRoot : scope;
            var parts = new List<string> { $"projectRoot={Uri.EscapeDataString(root)}" };
            if (!string.IsNullOrWhiteSpace(tier)) parts.Add($"tier={Uri.EscapeDataString(tier)}");
            url = $"api/project/memories/{id}?{string.Join("&", parts)}";
        }

        object payload = tags is null
            ? new { content, type, status }
            : new { content, type, status, tags };
        try
        {
            using var client = GetClient();
            var response = await client.PatchAsJsonAsync(url, payload, JsonOptions, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<MemoryDto>(JsonOptions, cancellationToken);
            }
            _logger.LogWarning("UpdateMemory failed with status {StatusCode}", response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating memory at {Url}", url);
        }

        return null;
    }

    /// <summary>
    /// Moves a memory between the shared (project) and local (project-local) tiers
    /// of one workspace — the dashboard's <c>moveTier</c>. The backend merges the
    /// entry into the target tier and drops the source.
    /// </summary>
    public async Task<bool> MoveMemoryTierAsync(string id, string sourceScope, string projectRoot, string targetTier, CancellationToken cancellationToken = default)
    {
        var endpoint = string.Equals(targetTier, "project-local", StringComparison.OrdinalIgnoreCase)
            ? "api/scoped/move-to-local"
            : "api/scoped/move-to-project";
        var payload = new
        {
            id,
            sourceScope,
            sourceProjectRoot = projectRoot,
            targetProjectRoot = projectRoot
        };

        try
        {
            using var client = GetClient();
            var response = await client.PostAsJsonAsync(endpoint, payload, JsonOptions, cancellationToken);
            if (response.IsSuccessStatusCode) return true;
            _logger.LogWarning("MoveMemoryTier failed with status {StatusCode}", response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error moving memory tier via {Endpoint}", endpoint);
        }

        return false;
    }

    public async Task<bool> DeleteMemoryAsync(string id, string scope = "all", string? projectRoot = null, string? tier = null, CancellationToken cancellationToken = default)
    {
        string url;
        if (string.Equals(scope, "global", StringComparison.OrdinalIgnoreCase))
        {
            url = $"api/global/memories/{id}";
        }
        else
        {
            var root = !string.IsNullOrWhiteSpace(projectRoot) ? projectRoot : scope;
            var parts = new List<string> { $"projectRoot={Uri.EscapeDataString(root)}" };
            if (!string.IsNullOrWhiteSpace(tier)) parts.Add($"tier={Uri.EscapeDataString(tier)}");
            url = $"api/project/memories/{id}?{string.Join("&", parts)}";
        }

        try
        {
            using var client = GetClient();
            var response = await client.DeleteAsync(url, cancellationToken);
            return response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NoContent;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting memory at {Url}", url);
            return false;
        }
    }

    public async Task<bool> PromoteToGlobalAsync(string id, string projectRoot, bool move = false, CancellationToken cancellationToken = default)
    {
        var payload = new { id, sourceProjectRoot = projectRoot, move };
        try
        {
            using var client = GetClient();
            var response = await client.PostAsJsonAsync("api/scoped/promote-to-global", payload, JsonOptions, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error promoting memory {Id} to global", id);
            return false;
        }
    }

    public async Task<bool> CopyToProjectAsync(string id, string sourceScope, string? sourceProjectRoot, string targetProjectRoot, bool move = false, CancellationToken cancellationToken = default)
    {
        var payload = new { id, sourceScope, sourceProjectRoot, targetProjectRoot, move };
        try
        {
            using var client = GetClient();
            var response = await client.PostAsJsonAsync("api/scoped/copy-to-project", payload, JsonOptions, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error copying memory {Id} to project {Project}", id, targetProjectRoot);
            return false;
        }
    }

    public async Task<IReadOnlyList<RuntimeDto>> GetRuntimesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = GetClient();
            var response = await client.GetAsync("api/coordinator/runtimes", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var dtos = await response.Content.ReadFromJsonAsync<List<RuntimeDto>>(JsonOptions, cancellationToken);
                return dtos ?? [];
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error loading runtimes");
        }

        return [];
    }

    public async Task<IReadOnlyList<CoordinatorRuntimeDto>> GetAllCoordinatorRuntimesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = GetClient();
            var response = await client.GetAsync("api/coordinator/runtimes?includeRegistered=true&allProcesses=true", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var dtos = await response.Content.ReadFromJsonAsync<List<CoordinatorRuntimeDto>>(JsonOptions, cancellationToken);
                return dtos ?? [];
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error loading coordinator runtimes");
        }

        return [];
    }

    public async Task<WorkspaceTreesDto?> GetWorkspaceTreesAsync(
        string? status = null,
        string? q = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = GetClient();
            var query = new List<string>();
            if (!string.IsNullOrWhiteSpace(status)) query.Add($"status={Uri.EscapeDataString(status.Trim())}");
            if (!string.IsNullOrWhiteSpace(q)) query.Add($"q={Uri.EscapeDataString(q.Trim())}");
            var url = query.Count > 0
                ? $"api/coordinator/workspaces?{string.Join("&", query)}"
                : "api/coordinator/workspaces";

            var response = await client.GetAsync(url, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<WorkspaceTreesDto>(JsonOptions, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error loading workspace trees");
        }

        return null;
    }

    public async Task<IReadOnlyList<ProjectCodebaseStatusDto>> GetProjectCodebaseStatusesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = GetClient();
            var response = await client.GetAsync("api/coordinator/projects/codebase-statuses", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var dtos = await response.Content.ReadFromJsonAsync<List<ProjectCodebaseStatusDto>>(JsonOptions, cancellationToken);
                return dtos ?? [];
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error loading codebase statuses");
        }

        return [];
    }

    public async Task<bool> ReindexCodebaseAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = GetClient();
            var url = $"api/codebase/rebuild-index?project={Uri.EscapeDataString(projectRoot)}";
            var response = await client.PostAsync(url, null, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error requesting reindex for {Project}", projectRoot);
            return false;
        }
    }

    public async Task<bool> DeleteProjectAsync(string projectRoot, bool deleteRegistry = true, bool deleteCodebaseIndex = true, bool deleteDotEling = false, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = GetClient();
            var payload = new
            {
                workspaceRoot = projectRoot,
                deleteRegistry,
                deleteCodebaseIndex,
                deleteDotEling
            };
            var response = await client.PostAsJsonAsync("api/coordinator/project/delete", payload, JsonOptions, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting project {Project}", projectRoot);
            return false;
        }
    }

    public sealed class ProviderConfig
    {
        public string? BaseUrl { get; set; }
        public string? Model { get; set; }
        public string? ApiKey { get; set; }
    }

    public sealed class ProviderTestResult
    {
        public bool Ok { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public sealed class HostDrive
    {
        public string Path { get; set; } = string.Empty;
        public string? Label { get; set; }
        public string? Type { get; set; }
        public long? SizeBytes { get; set; }
        public long? FreeBytes { get; set; }
    }


    public async Task<ProviderViewDto?> GetProviderAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = GetClient();
            var response = await client.GetAsync("api/agent/provider/", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<ProviderViewDto>(JsonOptions, cancellationToken);
            }
            _logger.LogWarning("GetProvider failed with status {StatusCode}", response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching provider");
        }
        return null;
    }

    public async Task<ProviderViewDto?> UpdateProviderAsync(string? baseUrl, string? model, string? apiKey, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = GetClient();
            var payload = new { baseUrl, model, apiKey };
            var response = await client.PutAsJsonAsync("api/agent/provider/", payload, JsonOptions, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<ProviderViewDto>(JsonOptions, cancellationToken);
            }
            _logger.LogWarning("UpdateProvider failed with status {StatusCode}", response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating provider");
        }
        return null;
    }

    public async Task<IReadOnlyList<string>> FetchModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = GetClient();
            var response = await client.PostAsJsonAsync("api/agent/provider/models", new { }, JsonOptions, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<ModelListDto>(JsonOptions, cancellationToken);
                return result?.Models ?? [];
            }
            _logger.LogWarning("FetchModels failed with status {StatusCode}", response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching models");
        }
        return [];
    }

    public async Task<ProviderTestResultDto?> TestProviderAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = GetClient();
            var response = await client.PostAsJsonAsync("api/agent/provider/test", new { }, JsonOptions, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<ProviderTestResultDto>(JsonOptions, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error testing provider");
        }
        return null;
    }

    public async Task<IReadOnlyList<string>?> GetWorkspacesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = GetClient();
            var response = await client.GetAsync("api/agent/workspaces/", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<WorkspaceListDto>(JsonOptions, cancellationToken);
                return result?.Roots ?? [];
            }
            _logger.LogWarning("GetWorkspaces failed with status {StatusCode}", response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching workspaces");
        }
        return null;
    }

    public async Task<bool> AddWorkspaceAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = GetClient();
            var response = await client.PostAsJsonAsync("api/agent/workspaces/", new { path }, JsonOptions, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding workspace");
            return false;
        }
    }

    public async Task<bool> RemoveWorkspaceAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = GetClient();
            var url = $"api/agent/workspaces/?path={Uri.EscapeDataString(path)}";
            var response = await client.DeleteAsync(url, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing workspace");
            return false;
        }
    }

    public async Task<IReadOnlyList<FileListEntryDto>> ListDirAsync(string root, string path, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = GetClient();
            var url = $"api/agent/files/list?root={Uri.EscapeDataString(root)}&path={Uri.EscapeDataString(path)}";
            var response = await client.GetAsync(url, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<FileListDto>(JsonOptions, cancellationToken);
                return result?.Entries ?? [];
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing directory");
        }
        return [];
    }

    public async Task<FileReadDto?> ReadFileAsync(string root, string path, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = GetClient();
            var url = $"api/agent/files/read?root={Uri.EscapeDataString(root)}&path={Uri.EscapeDataString(path)}";
            var response = await client.GetAsync(url, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<FileReadDto>(JsonOptions, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading file");
        }
        return null;
    }

    public async Task<bool> WriteFileAsync(string root, string path, string content, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = GetClient();
            var payload = new { root, path, content };
            var response = await client.PostAsJsonAsync("api/agent/files/write", payload, JsonOptions, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error writing file");
            return false;
        }
    }

    public async Task<IReadOnlyList<string>> GetHostDrivesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = GetClient();
            var response = await client.GetAsync("api/agent/host/drives", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<List<string>>(JsonOptions, cancellationToken);
                return result ?? [];
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching host drives");
        }
        return [];
    }

    public async Task<HostBrowseDto?> BrowseHostAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = GetClient();
            var url = $"api/agent/host/browse?path={Uri.EscapeDataString(path)}";
            var response = await client.GetAsync(url, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<HostBrowseDto>(JsonOptions, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error browsing host");
        }
        return null;
    }

    public async Task<IReadOnlyList<ChatSummaryDto>> GetChatsAsync(string workspace, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = GetClient();
            var url = $"api/agent/chats/?workspace={Uri.EscapeDataString(workspace)}";
            var response = await client.GetAsync(url, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<List<ChatSummaryDto>>(JsonOptions, cancellationToken);
                return result ?? [];
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching chats");
        }
        return [];
    }

    public async Task<IReadOnlyList<ChatMessageDto>> GetChatAsync(string id, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = GetClient();
            var response = await client.GetAsync($"api/agent/chats/{id}", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<List<ChatMessageDto>>(JsonOptions, cancellationToken);
                return result ?? [];
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching chat");
        }
        return [];
    }

    public async Task<TurnDto?> SendMessageAsync(string workspace, string message, string? chatId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = GetClient();
            var payload = new { workspace, message, chatId };
            var response = await client.PostAsJsonAsync("api/agent/chats", payload, JsonOptions, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<TurnDto>(JsonOptions, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending message");
        }
        return null;
    }

    public async Task SendStreamingAsync(string workspace, string message, string? chatId, Action<ChatStreamEvent> onEvent, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = GetClient();
            var payload = new { workspace, message, chatId };
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/agent/chats/stream")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);
            string? eventName = null;
            var dataBuilder = new StringBuilder();
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line == null) break;
                if (line.StartsWith("event:"))
                {
                    eventName = line.Substring("event:".Length).Trim();
                }
                else if (line.StartsWith("data:"))
                {
                    var part = line.Substring("data:".Length).TrimStart();
                    if (dataBuilder.Length > 0) dataBuilder.Append('\n');
                    dataBuilder.Append(part);
                }
                else if (string.IsNullOrWhiteSpace(line))
                {
                    if (eventName != null && dataBuilder.Length > 0)
                    {
                        var data = dataBuilder.ToString();
                        try
                        {
                            switch (eventName)
                            {
                                case "started":
                                    var s = JsonSerializer.Deserialize<StreamStarted>(data, JsonOptions);
                                    if (s != null) onEvent(s);
                                    break;
                                case "delta":
                                    var d = JsonSerializer.Deserialize<StreamDelta>(data, JsonOptions);
                                    if (d != null) onEvent(d);
                                    break;
                                case "tool":
                                    var t = JsonSerializer.Deserialize<StreamTool>(data, JsonOptions);
                                    if (t != null) onEvent(t);
                                    break;
                                case "tool_result":
                                    var tr = JsonSerializer.Deserialize<StreamToolResult>(data, JsonOptions);
                                    if (tr != null) onEvent(tr);
                                    break;
                                case "done":
                                    var done = JsonSerializer.Deserialize<StreamDone>(data, JsonOptions);
                                    if (done != null) onEvent(done);
                                    break;
                                case "failed":
                                    var f = JsonSerializer.Deserialize<StreamFailed>(data, JsonOptions);
                                    if (f != null) onEvent(f);
                                    break;
                            }
                        }
                        catch { /* ignore malformed */ }
                    }
                    eventName = null;
                    dataBuilder.Clear();
                }
                else
                {
                    if (dataBuilder.Length > 0) dataBuilder.Append('\n');
                    dataBuilder.Append(line);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Streaming error");
            onEvent(new StreamFailed(ex.Message));
        }
    }
}