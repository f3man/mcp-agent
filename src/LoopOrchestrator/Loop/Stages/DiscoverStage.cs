using LoopOrchestrator.Mcp;
using LoopOrchestrator.State;
using LoopOrchestrator.Telemetry;

namespace LoopOrchestrator.Loop.Stages;

/// <summary>
/// Stage 1 — Discover. Important upstream gap: McpServer's list_tenders/search_tenders have no
/// server-side "published since" filter, so any date-based check here can only ever be an
/// informational heuristic. The state store's already-seen-id exclusion is the SOLE authoritative
/// idempotency guard — see IdempotencyFilterTests.cs, which proves the date value is not
/// load-bearing.
/// </summary>
public sealed class DiscoverStage(IMcpTenderClient mcpClient, ITenderStateStore stateStore, ILogger<DiscoverStage> logger)
{
    public Task<IReadOnlyList<TenderSummary>> RunAsync(CancellationToken cancellationToken) =>
        LoopTelemetry.TraceStageAsync("discover", tenderId: null, async () =>
        {
            var lastRunAt = await stateStore.GetLastSuccessfulRunAtAsync(cancellationToken);
            var seenIds = await stateStore.GetSeenTenderIdsAsync(cancellationToken);

            var candidates = await mcpClient.ListTendersAsync(status: "active", limit: 100, cancellationToken: cancellationToken);

            var newTenders = FilterNewTenders(candidates, seenIds, lastRunAt);

            // Informational only — how many "new" (unseen) tenders have a startDate at or before
            // the last run. A non-zero count isn't an error; it just means Prozorro's data doesn't
            // perfectly line up with wall-clock run times, which is expected and fine — the seen-id
            // check above is what actually keeps reruns idempotent regardless.
            var olderThanLastRun = lastRunAt is null
                ? 0
                : newTenders.Count(t => t.TenderPeriod.StartDate is not null && t.TenderPeriod.StartDate <= lastRunAt);

            logger.LogInformation(
                "Discover: {CandidateCount} active tenders fetched, {SeenCount} already seen, {NewCount} new " +
                "({OlderThanLastRun} of which have a startDate at or before the last run — informational only).",
                candidates.Count, seenIds.Count, newTenders.Count, olderThanLastRun);

            return newTenders;
        });

    /// <summary>
    /// The seen-id check is the ONLY exclusion criterion — deliberately. An early version of this
    /// also hard-filtered by `lastRunAt` (dropping any candidate whose startDate predated it), but
    /// that risks wrongly excluding a genuinely new, unseen tender (missing/stale startDate, clock
    /// skew, a tender that only just entered "active" status despite an older nominal start date,
    /// etc.) — exactly the kind of silent-drop the idempotency guard must never cause. `lastRunAt`
    /// is accepted here only so a caller can log how many candidates look "old but still new" for
    /// visibility (see DiscoverStage.RunAsync); it never affects which tenders are returned.
    /// </summary>
    internal static IReadOnlyList<TenderSummary> FilterNewTenders(
        IReadOnlyList<TenderSummary> candidates, HashSet<string> seenIds, DateTimeOffset? lastRunAt) =>
        candidates.Where(t => !seenIds.Contains(t.Id)).ToList();
}
