using System.Net;
using System.Text;
using Elastic.Clients.Elasticsearch;
using Eling.Core.Database;

namespace Eling.Core.Tests;

/// <summary>
/// The ping-side refusal: an ES 7 cluster answers <c>GET /</c> happily, so the
/// version check is the only thing that turns "connected" into an explicit
/// refusal. ES 7 is out of scope for this client; the refusal beats an
/// obscure interface failure deep inside the typed transport.
/// </summary>
public sealed class ElasticsearchPingTests
{
    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public StubHttpMessageHandler(HttpResponseMessage response) => _response = response;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(_response);
    }

    [Fact]
    public async Task Ping_Es7_ThrowsUnsupportedVersion()
    {
        var pool = new Elastic.Transport.SingleNodePool(new Uri("http://elasticsearch-test:9200/"));
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"name":"n","cluster_name":"c","cluster_uuid":"u","version":{"number":"7.17.0","build_flavor":"default","lucene_version":"8.11.0"},"tagline":"You Know, for Search"}""",
                Encoding.UTF8,
                "application/json"),
        };
        response.Headers.Add("X-Elastic-Product", "Elasticsearch");
        var invoker = new Elastic.Transport.HttpRequestInvoker((_, _) => new StubHttpMessageHandler(response));
        var settings = new ElasticsearchClientSettings(pool, invoker)
            .RequestTimeout(TimeSpan.FromSeconds(5))
            .DisableDirectStreaming(true);
        var client = new ElasticsearchClient(settings);

        var service = new ElasticsearchReadService(new StubClientCache(client));
        var connection = DatabaseConnection.Create(
            "test",
            DatabaseProvider.Elasticsearch,
            "http://elasticsearch-test:9200/",
            DatabaseEnvironment.Development);

        var exception = await Assert.ThrowsAsync<NotSupportedException>(
            () => service.PingAsync(connection, CancellationToken.None));

        Assert.Equal(ElasticsearchRequirement.UnsupportedVersion, exception.Message);
    }

    private sealed class StubClientCache : IElasticsearchClientCache
    {
        private readonly ElasticsearchClient _client;

        public StubClientCache(ElasticsearchClient client) => _client = client;

        public ElasticsearchClient GetOrCreate(DatabaseConnection connection) => _client;

        public void Invalidate(string connectionId)
        {
        }
    }
}
