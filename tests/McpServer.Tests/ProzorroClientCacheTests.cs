using System.Net;
using System.Text;
using McpServer.Tenders;
using Microsoft.Extensions.Caching.Memory;

namespace McpServer.Tests;

public class ProzorroClientCacheTests
{
    [Fact]
    public async Task GetRecentTendersAsync_SecondCallWithinTtl_DoesNotHitNetworkAgain()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/tenders", StringComparison.Ordinal))
            {
                return Json("""{"data":[{"id":"t1","dateModified":"2026-01-01T00:00:00Z"}]}""");
            }
            if (path.EndsWith("/tenders/t1", StringComparison.Ordinal))
            {
                return Json("""{"data":{"id":"t1","title":"Test tender","status":"active.tendering"}}""");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var client = CreateClient(handler, TimeSpan.FromMinutes(5));

        var first = await client.GetRecentTendersAsync(CancellationToken.None);
        var callsAfterFirst = handler.CallCount;
        var second = await client.GetRecentTendersAsync(CancellationToken.None);

        Assert.Single(first);
        Assert.Equal("t1", first[0].Id);
        Assert.Same(first, second); // served straight from IMemoryCache, not just equal-by-value
        Assert.Equal(callsAfterFirst, handler.CallCount); // no additional HTTP calls on cache hit
        Assert.True(callsAfterFirst > 0);
    }

    [Fact]
    public async Task GetRecentTendersAsync_AfterTtlExpires_RefetchesFromNetwork()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/tenders", StringComparison.Ordinal))
            {
                return Json("""{"data":[{"id":"t1","dateModified":"2026-01-01T00:00:00Z"}]}""");
            }
            if (path.EndsWith("/tenders/t1", StringComparison.Ordinal))
            {
                return Json("""{"data":{"id":"t1","title":"Test tender","status":"active.tendering"}}""");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        // TTL effectively zero: the second call should treat the cache as expired.
        var client = CreateClient(handler, TimeSpan.FromMilliseconds(1));

        await client.GetRecentTendersAsync(CancellationToken.None);
        var callsAfterFirst = handler.CallCount;
        await Task.Delay(50);
        await client.GetRecentTendersAsync(CancellationToken.None);

        Assert.True(handler.CallCount > callsAfterFirst);
    }

    [Fact]
    public async Task GetTenderAsync_Upstream404_ThrowsTenderNotFoundException()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = CreateClient(handler, TimeSpan.FromMinutes(5));

        await Assert.ThrowsAsync<TenderNotFoundException>(() => client.GetTenderAsync("bogus-id", CancellationToken.None));
    }

    [Fact]
    public async Task GetTenderAsync_Found_ReturnsMappedDetail()
    {
        var handler = new StubHttpMessageHandler(_ =>
            Json("""{"data":{"id":"t1","title":"Test tender","status":"active.tendering","description":"fallback text"}}"""));
        var client = CreateClient(handler, TimeSpan.FromMinutes(5));

        var detail = await client.GetTenderAsync("t1", CancellationToken.None);

        Assert.Equal("t1", detail.Id);
        Assert.Equal("fallback text", detail.EligibilityText);
    }

    private static ProzorroClient CreateClient(StubHttpMessageHandler handler, TimeSpan ttl)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/api/2.5/") };
        var cache = new MemoryCache(new MemoryCacheOptions());
        var options = new ProzorroClientOptions("https://example.test/api/2.5", ttl);
        return new ProzorroClient(httpClient, cache, options);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(responder(request));
        }
    }
}
