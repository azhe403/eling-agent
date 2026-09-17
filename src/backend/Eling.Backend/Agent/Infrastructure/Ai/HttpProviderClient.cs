using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Eling.Backend.Agent.Ports;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Agent.Infrastructure.Ai;

public sealed class HttpProviderClient(HttpClient http, ILogger<HttpProviderClient> logger) : IProviderClient
{
    public async Task<IReadOnlyList<string>> ListModelsAsync(string baseUrl, string? apiKey, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/models");
            if (!string.IsNullOrEmpty(apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Provider models fetch failed with {(int)response.StatusCode}. Excerpt: {Excerpt(body)}");
            }

            try
            {
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                {
                    logger.LogWarning("Provider models response has no data array; returning empty list");
                    return [];
                }

                var models = new List<string>();
                foreach (var item in data.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    {
                        models.Add(id.GetString()!);
                    }
                }

                return models;
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Provider models response is not JSON; returning empty list");
                return [];
            }
        }
        catch (Exception ex) when (ex is not HttpRequestException)
        {
            logger.LogWarning(ex, "Provider models fetch failed");
            throw new HttpRequestException($"Provider models fetch failed: {ex.Message}", ex);
        }
    }

    public async Task<ProviderProbe> TestConnectionAsync(string baseUrl, string? apiKey, string? model, CancellationToken ct)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromSeconds(15));

            var payload = new
            {
                model = model ?? "default",
                messages = new[] { new { role = "user", content = "ping" } },
                max_tokens = 1
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/chat/completions")
            {
                Content = JsonContent.Create(payload)
            };
            if (!string.IsNullOrEmpty(apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            using var response = await http.SendAsync(request, linked.Token);
            var excerpt = await SafeExcerptAsync(response, ct);
            if (!response.IsSuccessStatusCode)
            {
                return new ProviderProbe(false, $"Provider returned {(int)response.StatusCode}. Excerpt: {excerpt}");
            }

            return new ProviderProbe(true, "Provider responded successfully.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Provider connection test failed");
            return new ProviderProbe(false, ex.Message);
        }
    }

    private static string Excerpt(string body) => body.Length > 200 ? body[..200] : body;

    private static async Task<string> SafeExcerptAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            return body.Length > 200 ? body[..200] : body;
        }
        catch
        {
            return string.Empty;
        }
    }
}
