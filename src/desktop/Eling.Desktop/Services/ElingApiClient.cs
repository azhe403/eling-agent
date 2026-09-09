using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Eling.Desktop.Models;
using Microsoft.Extensions.Logging;

namespace Eling.Desktop.Services;

public sealed class ElingApiClient(BackendSupervisor supervisor, ILogger<ElingApiClient> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private HttpClient GetClient()
    {
        var baseUrl = supervisor.BaseUrl ?? "http://127.0.0.1:4417";
        return new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/")
        };
    }

    public async Task<IReadOnlyList<MemoryDto>> GetMemoriesAsync(string scope = "all", int limit = 100, CancellationToken cancellationToken = default)
    {
        var url = scope switch
        {
            "global" => $"api/global/memories?limit={limit}",
            "all" => $"api/aggregated/memories?limit={limit}",
            _ => $"api/project/memories?projectRoot={Uri.EscapeDataString(scope)}&limit={limit}"
        };

        try
        {
            using var client = GetClient();
            var response = await client.GetAsync(url, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var dtos = await response.Content.ReadFromJsonAsync<List<MemoryDto>>(JsonOptions, cancellationToken);
                return dtos ?? [];
            }
            logger.LogWarning("GetMemories failed with status {StatusCode}", response.StatusCode);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching memories from {Url}", url);
        }

        return [];
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
            logger.LogWarning("CreateMemory failed with status {StatusCode}", response.StatusCode);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating memory at {Url}", url);
        }

        return null;
    }

    public async Task<MemoryDto?> UpdateMemoryAsync(string id, string? content, string? type, string? status, string scope = "all", CancellationToken cancellationToken = default)
    {
        var url = scope switch
        {
            "global" => $"api/global/memories/{id}",
            "all" => $"api/memories/{id}",
            _ => $"api/project/memories/{id}?projectRoot={Uri.EscapeDataString(scope)}"
        };

        var payload = new { content, type, status };
        try
        {
            using var client = GetClient();
            var response = await client.PatchAsJsonAsync(url, payload, JsonOptions, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<MemoryDto>(JsonOptions, cancellationToken);
            }
            logger.LogWarning("UpdateMemory failed with status {StatusCode}", response.StatusCode);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating memory at {Url}", url);
        }

        return null;
    }

    public async Task<bool> DeleteMemoryAsync(string id, string scope = "all", CancellationToken cancellationToken = default)
    {
        var url = scope switch
        {
            "global" => $"api/global/memories/{id}",
            "all" => $"api/memories/{id}",
            _ => $"api/project/memories/{id}?projectRoot={Uri.EscapeDataString(scope)}"
        };

        try
        {
            using var client = GetClient();
            var response = await client.DeleteAsync(url, cancellationToken);
            return response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NoContent;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting memory at {Url}", url);
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
            logger.LogError(ex, "Error promoting memory {Id} to global", id);
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
            logger.LogError(ex, "Error copying memory {Id} to project {Project}", id, targetProjectRoot);
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
            logger.LogWarning(ex, "Error loading runtimes");
        }

        return [];
    }
}
