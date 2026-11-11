using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Eling.Backend.Tests;

internal static class EnvelopeTestExtensions
{
    /// <summary>
    /// The drop-in replacement for the default test client: same base address and
    /// handler wiring as before, with the envelope unwrapped on success.
    /// </summary>
    public static HttpClient CreateEnvelopeClient(this WebApplication app)
    {
        var server = (TestServer)app.Services.GetRequiredService<IServer>();
        var handler = new EnvelopeTestHandler { InnerHandler = server.CreateHandler() };
        return new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
    }
}
