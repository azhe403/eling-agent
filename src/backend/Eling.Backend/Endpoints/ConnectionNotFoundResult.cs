using Eling.Backend.Bootstrap;
using Microsoft.AspNetCore.Http;

namespace Eling.Backend.Endpoints;

/// <summary>
/// A 404 the client can act on: the connection id is not in the registry.
/// The body is what the dashboard shows - it names the connection, says what
/// happened and what to do; the envelope carries the stable machine code.
/// </summary>
internal static class ConnectionNotFoundResult
{
    public static IResult Create(string id, HttpContext httpContext)
    {
        httpContext.Items[ApiEnvelopeMiddleware.ErrorCodeItem] = "connection_not_found";
        return Results.NotFound(new { error = $"Connection '{id}' is gone - it may have been deleted in another tab or by another eling app; the connection list is being refreshed." });
    }
}
