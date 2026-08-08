using System.ComponentModel;
using McpServer.Telemetry;
using McpServer.Tenders;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace McpServer.Tools;

[McpServerToolType]
public sealed class TenderTools(IProzorroClient prozorroClient, ILogger<TenderTools> logger)
{
    [McpServerTool(Name = "list_tenders", ReadOnly = true)]
    [Description("List recently published tenders, optionally filtered by category, region, and status.")]
    public Task<IReadOnlyList<TenderSummary>> ListTenders(
        [Description("Free-text CPV category filter")] string? category = null,
        [Description("Free-text region filter")] string? region = null,
        [Description("Tender status filter. Default 'active' matches any active.* upstream status.")] string status = "active",
        [Description("Max results to return, default 20, max 100")] int limit = 20,
        CancellationToken cancellationToken = default) =>
        ToolTelemetry.TraceAsync(
            "list_tenders",
            new Dictionary<string, object?> { ["category"] = category, ["region"] = region, ["status"] = status, ["limit"] = limit },
            async () =>
            {
                var recent = await prozorroClient.GetRecentTendersAsync(cancellationToken);
                return TenderFilter.Apply(recent, category, region, status, limit);
            });

    [McpServerTool(Name = "get_tender", ReadOnly = true)]
    [Description("Full detail for one tender, including its raw eligibility requirements text.")]
    public Task<TenderDetail> GetTender(
        [Description("The Prozorro tender id")] string tenderId,
        CancellationToken cancellationToken = default) =>
        ToolTelemetry.TraceAsync(
            "get_tender",
            new Dictionary<string, object?> { ["tenderId"] = tenderId },
            async () =>
            {
                try
                {
                    return await prozorroClient.GetTenderAsync(tenderId, cancellationToken);
                }
                catch (TenderNotFoundException ex)
                {
                    // Structured error log, correlated to the failed span above by OTel's
                    // logging<->tracing integration — this is what makes a deliberately bad
                    // tenderId show up as an error-level log entry in the dashboard, not just an
                    // MCP-level error response.
                    logger.LogError(ex, "get_tender failed: tender {TenderId} not found", tenderId);
                    // Translate into a clean, client-visible MCP tool error rather than letting a
                    // raw exception surface (which the SDK would otherwise report with a generic message).
                    throw new McpException(ex.Message);
                }
            });

    [McpServerTool(Name = "search_tenders", ReadOnly = true)]
    [Description("Free-text search for tenders across title and category.")]
    public Task<IReadOnlyList<TenderSummary>> SearchTenders(
        [Description("Search keywords")] string keywords,
        [Description("Max results to return, default 20")] int limit = 20,
        CancellationToken cancellationToken = default) =>
        ToolTelemetry.TraceAsync(
            "search_tenders",
            new Dictionary<string, object?> { ["keywords"] = keywords, ["limit"] = limit },
            async () =>
            {
                var recent = await prozorroClient.GetRecentTendersAsync(cancellationToken);
                return TenderFilter.Search(recent, keywords, limit);
            });
}
