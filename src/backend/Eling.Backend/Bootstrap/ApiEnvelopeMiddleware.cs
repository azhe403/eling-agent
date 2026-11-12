using System.Buffers;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Bootstrap;

/// <summary>
/// One middleware, two guarantees (see docs/superpowers/specs/2026-11-11-standard-envelope-design.md):
///
/// 1. No JSON response is ever zero-byte except on 204/304/HEAD (the HTTP contract).
/// 2. Every JSON success is `{ code, message, data }`; every ≥400 is the same shape,
///    regardless of the endpoint's content type — invalid bodies even lose their
///    original content type and become `application/json`.
///
/// The pass-through decision is made at the FIRST write by the wrapper stream, not
/// after the pipeline completes: HEAD, 204/304, text/event-stream, and success-path
/// non-JSON bodies pass through untouched, so streaming endpoints keep their live
/// `FlushAsync` semantics. Everything else buffers and is transformed on completion.
/// </summary>
public sealed class ApiEnvelopeMiddleware
{
    /// <summary>An endpoint may set this to a lower_snake_case code; it wins over the status-derived generic.</summary>
    public const string ErrorCodeItem = "errorCode";

    private static readonly JsonSerializerOptions CompactJson = new(JsonSerializerDefaults.Web);

    private readonly RequestDelegate _next;
    private readonly ILogger<ApiEnvelopeMiddleware> _logger;

    public ApiEnvelopeMiddleware(RequestDelegate next, ILogger<ApiEnvelopeMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var originalBody = context.Response.Body;
        var buffer = new TransformingResponseBodyStream(context, originalBody, _logger);
        context.Response.Body = buffer;
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            if (buffer.Passthrough)
            {
                // Headers and partial frames are already on the wire; the response is what it is.
                _logger.LogWarning(ex, "Unhandled exception on a pass-through stream ({Method} {Path}); client sees a torn stream", context.Request.Method, context.Request.Path);
                throw;
            }
            // Nothing leaked to the client; answer with a self-contained 500 envelope.
            buffer.Discard();
            _logger.LogWarning(ex, "Unhandled exception ({Method} {Path}); answered with a 500 envelope instead", context.Request.Method, context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json; charset=utf-8";
            await WriteEnvelopeAsync(context, originalBody, "internal_error", ex.Message, data: null);
            return;
        }
        await buffer.CompleteAsync(context, originalBody);
    }

    /// <summary>Writes a compact envelope with the exact key order `code`, `message`, `data`.</summary>
    internal static async Task WriteEnvelopeAsync(HttpContext context, Stream target, string code, string message, JsonNode? data)
    {
        var envelope = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
            ["data"] = data,
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, CompactJson);
        context.Response.StatusCode = context.Response.StatusCode is >= 100 and <= 599 ? context.Response.StatusCode : 500;
        context.Response.ContentLength = bytes.Length;
        await target.WriteAsync(bytes);
        await target.FlushAsync();
    }

    private static string VerbSuccessMessage(string method) => method.ToUpperInvariant() switch
    {
        "GET" or "HEAD" => "The resource was read successfully from the server.",
        "POST" or "PUT" or "PATCH" => "The request was processed successfully by the server.",
        "DELETE" => "The resource was deleted successfully by the server.",
        _ => "The request was completed successfully by the server.",
    };

    private static string GenericCode(int status) => status switch
    {
        400 => "bad_request",
        401 => "unauthorized",
        403 => "forbidden",
        404 => "not_found",
        405 => "method_not_allowed",
        409 => "conflict",
        413 => "payload_too_large",
        414 => "uri_too_long",
        415 => "unsupported_media_type",
        429 => "too_many_requests",
        _ when status >= 500 => "internal_error",
        _ => "http_error",
    };

    private static string EmptyErrorProse(int status) => status switch
    {
        400 => "The request was rejected - check the input and try again.",
        401 => "You are not allowed to do this yet - sign in and try again.",
        403 => "You are not allowed to do this - the request was rejected.",
        404 => "The requested resource does not exist (anymore) - refresh and try again.",
        405 => "That HTTP method is not supported on this route.",
        415 => "That media type is not supported by this endpoint.",
        429 => "Too many requests - wait a moment and try again.",
        500 => "Something went wrong on the server - retry, and check the log for details.",
        502 or 503 => "The upstream service is unavailable - try again shortly.",
        _ => $"The request failed with HTTP {status} - retry, and check the log for details.",
    };

    /// <summary>
    /// Decides at first write whether to pass through (SSE, non-JSON success)
    /// or buffer for transformation. Buffering SSE would silently freeze every
    /// `FlushAsync` into one never-delivered body.
    /// </summary>
    private sealed class TransformingResponseBodyStream : Stream
    {
        private readonly HttpContext _context;
        private readonly Stream _original;
        private readonly MemoryStream _buffer = new();
        private readonly ILogger _logger;

        private bool _decided;
        private bool _passthrough;

        public TransformingResponseBodyStream(HttpContext context, Stream original, ILogger logger)
        {
            _context = context;
            _original = original;
            _logger = logger;
        }

        public bool Passthrough => _passthrough;
        public void Discard() => _buffer.SetLength(0);

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        private void DecideIfNeeded()
        {
            if (_decided) return;
            _decided = true;
            var response = _context.Response;
            var contentType = response.ContentType;
            var method = _context.Request.Method;
            var status = response.StatusCode;
            var isHead = string.Equals(method, HttpMethods.Head, StringComparison.OrdinalIgnoreCase);
            var noBodyAllowed = status is StatusCodes.Status204NoContent or StatusCodes.Status304NotModified;
            var isJson = contentType is not null
                && (contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase)
                    || contentType.Contains("+json", StringComparison.OrdinalIgnoreCase)
                    || contentType.Contains("application/problem+json", StringComparison.OrdinalIgnoreCase));
            _passthrough = !isHead && !noBodyAllowed && status < 400 && !isJson;
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            DecideIfNeeded();
            if (_passthrough) _original.Write(buffer); else _buffer.Write(buffer);
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            DecideIfNeeded();
            if (_passthrough) await _original.WriteAsync(buffer.AsMemory(offset, count), ct); else await _buffer.WriteAsync(buffer.AsMemory(offset, count), ct);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            DecideIfNeeded();
            if (_passthrough) await _original.WriteAsync(buffer, ct); else await _buffer.WriteAsync(buffer, ct);
        }

        public override void WriteByte(byte value) => Write(new[] { value });

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            DecideIfNeeded();
            return _passthrough ? _original.FlushAsync(cancellationToken) : Task.CompletedTask;
        }

        public override void Flush()
        {
            DecideIfNeeded();
            if (_passthrough) _original.Flush();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _buffer.Dispose();
            base.Dispose(disposing);
        }

        /// <summary>
        /// Runs after the pipeline completes: transforms what was buffered, or does nothing
        /// for a pass-through stream. HEAD/204/304 end with headers only.
        /// </summary>
        public async Task CompleteAsync(HttpContext context, Stream original)
        {
            var response = context.Response;
            if (_passthrough)
            {
                // Everything already went out live (SSE, etc.).
                return;
            }
            if (string.Equals(context.Request.Method, HttpMethods.Head, StringComparison.OrdinalIgnoreCase))
            {
                // HEAD must carry headers but never bytes.
                response.ContentLength = _buffer.Length;
                return;
            }
            if (response.StatusCode is StatusCodes.Status204NoContent or StatusCodes.Status304NotModified)
            {
                return;
            }
            var bodyBytes = _buffer.ToArray();
            string? contentType = response.ContentType;
            if (response.StatusCode >= 200 && response.StatusCode < 300 && bodyBytes.Length == 0)
            {
                // 2xx with an empty body: bare Ok() and friends still get the uniform shape.
                var successMessage = VerbSuccessMessage(context.Request.Method);
                response.ContentType = "application/json; charset=utf-8";
                await WriteEnvelopeAsync(context, original, "ok", successMessage, data: null);
                return;
            }
            if (response.StatusCode >= 400)
            {
                var answer = NormalizeError(response, bodyBytes, context);
                response.ContentType = "application/json; charset=utf-8";
                await WriteEnvelopeAsync(context, original, answer.Code, answer.Message, data: null);
                return;
            }
            if (IsJsonContent(contentType))
            {
                var successMessage = VerbSuccessMessage(context.Request.Method);
                JsonNode? dataNode;
                try
                {
                    dataNode = bodyBytes.Length == 0 ? null : JsonNode.Parse(bodyBytes);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Malformed JSON on a JSON-typed response ({StatusCode}, {Path}) - answering with internal_error", response.StatusCode, context.Request.Path);
                    response.StatusCode = StatusCodes.Status500InternalServerError;
                    response.ContentType = "application/json; charset=utf-8";
                    await WriteEnvelopeAsync(context, original, "internal_error", "The endpoint returned a response that was not valid JSON - retry, and check the log for details.", data: null);
                    return;
                }
                response.ContentType = "application/json; charset=utf-8";
                await WriteEnvelopeAsync(context, original, "ok", successMessage, dataNode);
                return;
            }
            // Success-path non-JSON (text/plain, SSE that never flipped, etc.): copy through raw.
            response.ContentLength = bodyBytes.Length;
            await original.WriteAsync(bodyBytes);
        }

        private readonly record struct ErrorAnswer(
            string Code,
            string Message);

        private ErrorAnswer NormalizeError(HttpResponse response, byte[] body, HttpContext context)
        {
            var domainCode = context.Items[ErrorCodeItem] as string;
            var code = domainCode ?? GenericCode(response.StatusCode);
            var message = ExtractMessage(response, body) ?? EmptyErrorProse(response.StatusCode);
            return new ErrorAnswer(code, message);
        }

        private string? ExtractMessage(HttpResponse response, byte[] body)
        {
            if (body.Length == 0) return null;
            var text = Encoding.UTF8.GetString(body).Trim();
            if (text.Length == 0) return null;
            // JSON string/object with a message-ish field wins.
            if (IsJsonContent(response.ContentType) || text.StartsWith('{') || text.StartsWith('"'))
            {
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    var root = doc.RootElement;
                    if (root.ValueKind == JsonValueKind.String)
                    {
                        return Cap(root.GetString());
                    }
                    if (root.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var property in new[] { "message", "detail", "error", "title" })
                        {
                            if (root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
                            {
                                return Cap(value.GetString());
                            }
                        }
                    }
                    return null;
                }
                catch (JsonException ex)
                {
                    // Not JSON after all; fall back to the raw text so the reader
                    // still gets whatever the endpoint said.
                    _logger?.LogDebug(ex, "Body ({StatusCode}) did not parse as JSON; using raw text", response.StatusCode);
                }
            }
            return Cap(text);
        }

        private static bool IsJsonContent(string? contentType) =>
            contentType is not null
            && (contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("+json", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("application/problem+json", StringComparison.OrdinalIgnoreCase));

        private static string Cap(string? value)
        {
            var text = value?.Trim() ?? string.Empty;
            return text.Length <= 500 ? text : string.Concat(text.AsSpan(0, 497), "...");
        }
    }
}
