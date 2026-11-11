using System.Net;
using System.Text;
using System.Text.Json;

namespace Eling.Backend.Tests;

/// <summary>
/// Every real consumer of the Eling API unwraps the standard envelope before
/// touching the payload — the dashboard through its apiFetch helper, the desktop
/// client through its ReadEnvelopeAsync. This handler teaches the integration
/// tests the same contract in one place, so the ~110 call sites that were
/// written against raw DTOs keep passing unchanged.
///
/// 2xx JSON bodies shaped like { code, message, data } have their `data` span
/// substituted as the response body; everything else (errors, non-JSON, empty
/// bodies) passes through untouched. Error payloads never unwrap — the failure
/// envelope is the contract there.
/// </summary>
internal sealed class EnvelopeTestHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        if (!IsSuccessJson(response))
        {
            return response;
        }
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!TryUnwrap(raw, out var inner))
        {
            response.Content = new StringContent(raw, Encoding.UTF8, "application/json");
            return response;
        }
        response.Content = new StringContent(inner, Encoding.UTF8, "application/json");
        return response;
    }

    private static bool IsSuccessJson(HttpResponseMessage response)
    {
        if (response.StatusCode is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices) return false;
        var mediaType = response.Content?.Headers.ContentType?.MediaType;
        return mediaType is not null && (mediaType == "application/json" || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase));
    }

    internal static bool TryUnwrap(string raw, out string inner)
    {
        inner = raw;
        if (string.IsNullOrWhiteSpace(raw)) return false; // empty 2xx stays empty (no payload to substitute)
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.String) return false;
            if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String) return false;
            if (!root.TryGetProperty("data", out var data)) return false;
            inner = data.GetRawText();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

