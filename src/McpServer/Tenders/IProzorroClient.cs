namespace McpServer.Tenders;

public interface IProzorroClient
{
    /// <summary>
    /// Recently-published/modified tenders, cached in-memory for TENDER_CACHE_SECONDS. Backs both
    /// list_tenders and search_tenders (both filter/search over the same cached batch).
    /// </summary>
    Task<IReadOnlyList<TenderSummary>> GetRecentTendersAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Full detail for one tender. Throws <see cref="TenderNotFoundException"/> if the upstream
    /// API 404s or returns an empty payload for the given id.
    /// </summary>
    Task<TenderDetail> GetTenderAsync(string tenderId, CancellationToken cancellationToken);
}
