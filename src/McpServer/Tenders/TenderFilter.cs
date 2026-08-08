namespace McpServer.Tenders;

/// <summary>
/// In-memory filtering/search over an already-fetched batch of tenders. Pure and dependency-free
/// so it's trivially unit-testable (see TenderFilterTests.cs).
/// </summary>
internal static class TenderFilter
{
    private const int DefaultLimit = 20;
    private const int MaxLimit = 100;

    public static IReadOnlyList<TenderSummary> Apply(
        IReadOnlyList<TenderSummary> source, string? category, string? region, string status, int limit)
    {
        IEnumerable<TenderSummary> query = source;

        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(t => Contains(t.CpvCategory, category));

        if (!string.IsNullOrWhiteSpace(region))
            query = query.Where(t => Contains(t.ProcuringEntity.Region, region));

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(t => MatchesStatus(t.Status, status));

        return query.Take(ClampLimit(limit)).ToList();
    }

    public static IReadOnlyList<TenderSummary> Search(IReadOnlyList<TenderSummary> source, string keywords, int limit)
    {
        var terms = keywords.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0)
            return source.Take(ClampLimit(limit)).ToList();

        return source
            .Where(t => terms.All(term =>
                Contains(t.Title, term) || Contains(t.TitleEn, term) || Contains(t.CpvCategory, term)))
            .Take(ClampLimit(limit))
            .ToList();
    }

    /// <summary>
    /// The default status filter "active" matches any upstream status starting with "active"
    /// (bare "active", or dotted sub-statuses like "active.tendering", "active.auction",
    /// "active.enquiries" — all confirmed live against the real API) since that's what "active"
    /// means in this domain. Any other requested value is matched exactly (case-insensitively)
    /// against the upstream status.
    /// </summary>
    private static bool MatchesStatus(string upstreamStatus, string requestedStatus) =>
        string.Equals(requestedStatus, "active", StringComparison.OrdinalIgnoreCase)
            ? upstreamStatus.StartsWith("active", StringComparison.OrdinalIgnoreCase)
            : string.Equals(upstreamStatus, requestedStatus, StringComparison.OrdinalIgnoreCase);

    private static bool Contains(string? haystack, string needle) =>
        !string.IsNullOrEmpty(haystack) && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>Clamps a requested limit into [1, 100], the range documented in docs/01-mcp-server.md.</summary>
    internal static int ClampLimit(int limit) => Math.Clamp(limit <= 0 ? DefaultLimit : limit, 1, MaxLimit);
}
