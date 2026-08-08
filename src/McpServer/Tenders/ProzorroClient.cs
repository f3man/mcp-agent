using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using McpServer.Tenders.Prozorro;
using Microsoft.Extensions.Caching.Memory;

namespace McpServer.Tenders;

/// <summary>
/// Talks to the public Prozorro API (https://public.api.openprocurement.org). "Recent tenders"
/// are fetched, mapped, and cached as a single in-memory batch shared by list_tenders and
/// search_tenders; get_tender always hits the API directly since detail lookups are cheap and
/// callers expect fresh data for a specific id.
///
/// Registered via AddHttpClient&lt;IProzorroClient, ProzorroClient&gt;() (see Program.cs), which
/// means new instances are created per-request per standard IHttpClientFactory guidance — the
/// refresh lock and request throttle are therefore `static` so they apply process-wide, not just
/// within a single instance.
/// </summary>
public sealed class ProzorroClient(HttpClient httpClient, IMemoryCache cache, ProzorroClientOptions options) : IProzorroClient
{
    private const string CacheKey = "prozorro:recent-tenders";

    // "Polite client" bounds: we scan the most-recently-modified tenders, not the full multi-year
    // history — consistent with the spec's framing of "recently published tenders".
    private const int MaxTendersToScan = 150;
    private const int PageSize = 100;
    private const int MaxPages = 3;

    private static readonly JsonSerializerOptions UpstreamJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly SemaphoreSlim RefreshLock = new(1, 1);
    private static readonly SemaphoreSlim DetailThrottle = new(8);

    public async Task<IReadOnlyList<TenderSummary>> GetRecentTendersAsync(CancellationToken cancellationToken)
    {
        if (cache.TryGetValue<IReadOnlyList<TenderSummary>>(CacheKey, out var cached) && cached is not null)
            return cached;

        // Guard against a cache-stampede: if several tool calls race in after the cache expires,
        // only the first refetches; the rest wait and then reuse its result.
        await RefreshLock.WaitAsync(cancellationToken);
        try
        {
            if (cache.TryGetValue<IReadOnlyList<TenderSummary>>(CacheKey, out cached) && cached is not null)
                return cached;

            var fetched = await FetchRecentAsync(cancellationToken);
            cache.Set(CacheKey, fetched, options.CacheTtl);
            return fetched;
        }
        finally
        {
            RefreshLock.Release();
        }
    }

    public async Task<TenderDetail> GetTenderAsync(string tenderId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync($"tenders/{Uri.EscapeDataString(tenderId)}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new TenderNotFoundException(tenderId);

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<ProzorroTenderResponse>(UpstreamJsonOptions, cancellationToken);
        if (payload?.Data is null)
            throw new TenderNotFoundException(tenderId);

        return ProzorroMapper.ToDetail(payload.Data);
    }

    private async Task<IReadOnlyList<TenderSummary>> FetchRecentAsync(CancellationToken cancellationToken)
    {
        var summaries = new List<TenderSummary>();
        string? offset = null;

        for (var page = 0; page < MaxPages && summaries.Count < MaxTendersToScan; page++)
        {
            // descending=1 returns most-recently-modified tenders first — the correct call for
            // "recently published tenders" (the undocumented default order is oldest-first).
            var url = offset is null
                ? $"tenders?descending=1&limit={PageSize}"
                : $"tenders?descending=1&limit={PageSize}&offset={Uri.EscapeDataString(offset)}";

            var listPage = await httpClient.GetFromJsonAsync<ProzorroListResponse>(url, UpstreamJsonOptions, cancellationToken);
            if (listPage is null || listPage.Data.Count == 0)
                break;

            var details = await Task.WhenAll(listPage.Data.Select(item => FetchDetailSafeAsync(item.Id, cancellationToken)));
            summaries.AddRange(details.Where(d => d is not null)!);

            offset = listPage.NextPage?.Offset;
            if (offset is null)
                break;
        }

        return summaries;
    }

    /// <summary>
    /// Fetches and maps one tender's detail for the batch listing, tolerating individual
    /// failures — one bad/removed record shouldn't fail the whole "recent tenders" batch.
    /// </summary>
    private async Task<TenderSummary?> FetchDetailSafeAsync(string id, CancellationToken cancellationToken)
    {
        await DetailThrottle.WaitAsync(cancellationToken);
        try
        {
            using var response = await httpClient.GetAsync($"tenders/{id}", cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            var payload = await response.Content.ReadFromJsonAsync<ProzorroTenderResponse>(UpstreamJsonOptions, cancellationToken);
            return payload?.Data is null ? null : ProzorroMapper.ToSummary(payload.Data);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested is false)
        {
            // Network hiccup on a single detail fetch — skip it rather than failing the batch.
            return null;
        }
        finally
        {
            DetailThrottle.Release();
        }
    }
}
