using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Updates;

/// <summary>
/// Reads the public GitHub releases list and picks the newest non-draft entry.
/// Fail-soft by contract: any transport or shape problem is logged and yields
/// <c>null</c>, never an exception, so callers simply keep the last known status.
/// </summary>
public sealed class GitHubReleaseClient
{
    private const string ReleasesUrl =
        "https://api.github.com/repos/azhe403/eling-agent/releases?per_page=20";

    private const int MaxNotesLength = 500;

    private readonly HttpClient _httpClient;
    private readonly ILogger<GitHubReleaseClient> _logger;

    public GitHubReleaseClient(HttpClient httpClient, ILogger<GitHubReleaseClient> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// Returns the newest non-draft release, or <c>null</c> when the list is
    /// empty, unreachable, or unparsable. Caller cancellation still throws.
    /// </summary>
    public async Task<ReleaseCandidate?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesUrl);
            request.Headers.UserAgent.ParseAdd("eling");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("GitHub releases check failed with HTTP {StatusCode}", (int)response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return SelectCandidate(document.RootElement);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GitHub releases check failed");
            return null;
        }
    }

    private static ReleaseCandidate? SelectCandidate(JsonElement releases)
    {
        if (releases.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        // The API returns newest first, so the first non-draft entry wins.
        foreach (var release in releases.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean())
            {
                continue;
            }

            var tag = release.TryGetProperty("tag_name", out var tagProperty)
                ? tagProperty.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(tag))
            {
                continue;
            }

            var url = release.TryGetProperty("html_url", out var urlProperty)
                ? urlProperty.GetString()
                : null;
            var notes = release.TryGetProperty("body", out var bodyProperty)
                ? bodyProperty.GetString()
                : null;
            return new ReleaseCandidate { Tag = tag, Url = url, Notes = Truncate(notes) };
        }

        return null;
    }

    private static string? Truncate(string? notes)
    {
        if (string.IsNullOrEmpty(notes))
        {
            return null;
        }

        var trimmed = notes.Trim();
        return trimmed.Length <= MaxNotesLength ? trimmed : trimmed[..MaxNotesLength];
    }
}
