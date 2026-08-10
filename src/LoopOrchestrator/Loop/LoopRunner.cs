using LoopOrchestrator.Loop.Stages;
using LoopOrchestrator.Mcp;
using LoopOrchestrator.State;
using LoopOrchestrator.Telemetry;

namespace LoopOrchestrator.Loop;

public sealed record RunResult(bool Started, int Processed, int Skipped, int Verified, int HandedOff, int Failed = 0);

/// <summary>
/// The actual 4-stage pipeline (Discover → Assess → Persist → Handoff), called by both
/// LoopBackgroundWorker's timer and the /run-now endpoint. Registered Scoped — see
/// Mcp/McpTenderClient.cs's remarks on why a fresh McpClient session per run (rather than one for
/// the whole process lifetime) is the better fit here; ASP.NET Core creates a scope per HTTP
/// request automatically (covering /run-now), and LoopBackgroundWorker creates one explicitly per
/// timer tick.
/// </summary>
public sealed class LoopRunner(
    IMcpTenderClient mcpClient,
    ITenderStateStore stateStore,
    DiscoverStage discoverStage,
    AssessStage assessStage,
    PersistStage persistStage,
    HandoffStage handoffStage,
    LoopOptions options,
    ILogger<LoopRunner> logger)
{
    // Process-wide "only one run at a time" guard. LoopRunner itself is Scoped (a new instance per
    // run), but this mutual-exclusion guarantee has to survive across those separate instances —
    // same reasoning as McpServer/Tenders/ProzorroClient.cs's static RefreshLock.
    private static readonly SemaphoreSlim RunGate = new(1, 1);

    public async Task<RunResult> TryRunOnceAsync(CancellationToken cancellationToken)
    {
        if (!await RunGate.WaitAsync(0, cancellationToken))
        {
            logger.LogInformation("A loop run is already in progress — skipping this trigger.");
            return new RunResult(Started: false, 0, 0, 0, 0);
        }

        try
        {
            return await RunAsync(cancellationToken);
        }
        finally
        {
            RunGate.Release();
        }
    }

    private async Task<RunResult> RunAsync(CancellationToken cancellationToken)
    {
        using var runActivity = LoopTelemetry.StartRunActivity();

        var candidates = await discoverStage.RunAsync(cancellationToken);

        // Guardrail from the prompt book: if a single run's candidate set is anomalously large,
        // stop rather than silently burning LLM budget — don't process ANY of them this run, so
        // the next run retries the same discovery once whatever caused the spike is investigated.
        if (candidates.Count > options.MaxTendersPerRun)
        {
            logger.LogWarning(
                "Discover found {Count} new tenders, exceeding MAX_TENDERS_PER_RUN={Max} — stopping this run " +
                "without processing any of them. Investigate before the next run.",
                candidates.Count, options.MaxTendersPerRun);
            return new RunResult(Started: true, 0, 0, 0, 0);
        }

        if (candidates.Count == 0)
        {
            logger.LogInformation("Discover found no new tenders — nothing to process this run.");
            await stateStore.SetLastSuccessfulRunAtAsync(DateTimeOffset.UtcNow, cancellationToken);
            return new RunResult(Started: true, 0, 0, 0, 0);
        }

        var companyProfile = await mcpClient.GetCompanyProfileAsync(cancellationToken);

        int skipped = 0, verified = 0, handedOff = 0, failed = 0;
        foreach (var tender in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // One tender's failure (a transient LLM/network error, a malformed response, etc.)
            // must not sink the rest of the batch — every prior tender's progress was already
            // persisted incrementally inside ProcessTenderAsync, and every later tender still
            // deserves its own attempt. Log and move on; the failed tender stays un-persisted for
            // this run and is naturally retried next run since it was never marked "seen" here
            // (PersistStage is what makes a tender show up in GetSeenTenderIdsAsync).
            try
            {
                switch (await ProcessTenderAsync(tender, companyProfile, cancellationToken))
                {
                    case TenderReviewStatus.Skipped: skipped++; break;
                    case TenderReviewStatus.Verified: verified++; break;
                    case TenderReviewStatus.HandedOff: handedOff++; break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                logger.LogError(ex, "Tender {TenderId} failed during processing — skipping, will retry next run.", tender.Id);
            }
        }

        await stateStore.SetLastSuccessfulRunAtAsync(DateTimeOffset.UtcNow, cancellationToken);

        logger.LogInformation(
            "Loop run complete: {Processed} processed, {Skipped} skipped, {Verified} verified, {HandedOff} handed off, {Failed} failed.",
            candidates.Count, skipped, verified, handedOff, failed);

        return new RunResult(Started: true, candidates.Count, skipped, verified, handedOff, failed);
    }

    private async Task<string> ProcessTenderAsync(TenderSummary tender, CompanyProfileData companyProfile, CancellationToken cancellationToken)
    {
        var firstSeenAt = DateTimeOffset.UtcNow; // stable across every persist call below for this tender

        var assessResult = await assessStage.RunAsync(tender, companyProfile, cancellationToken);

        if (!assessResult.Relevant)
        {
            await persistStage.RunAsync(
                new TenderReviewRecord(
                    tender.Id, firstSeenAt, TenderReviewStatus.Skipped, assessResult.RelevanceScore,
                    EligibilityVerdict: null, EligibilityRationale: null, HandoffSentAt: null, HumanDecision: null,
                    Notes: assessResult.RelevanceReason),
                cancellationToken);
            return TenderReviewStatus.Skipped;
        }

        // "limited" procurement method tenders can't realistically be bid on by an outside
        // supplier regardless of eligibility — checked here, right after Assess (the earliest
        // point TenderDetail.ProcurementMethod is available), before any persist for this tender
        // and before ever reaching Handoff, so no LLM call or Slack noise is spent on one.
        if (ProcurementMethodPolicy.IsExcluded(assessResult.TenderDetail.ProcurementMethod))
        {
            await persistStage.RunAsync(
                new TenderReviewRecord(
                    tender.Id, firstSeenAt, TenderReviewStatus.Skipped, assessResult.RelevanceScore,
                    assessResult.Verdict, assessResult.Rationale, HandoffSentAt: null, HumanDecision: null,
                    Notes: $"Excluded — procurementMethod={assessResult.TenderDetail.ProcurementMethod} (invite-only, not biddable)."),
                cancellationToken);
            return TenderReviewStatus.Skipped;
        }

        // Interim persist right after Assess, before the Handoff decision — matches the spec's
        // stage ordering (Persist, then Handoff). The final persist below may overwrite Status.
        await persistStage.RunAsync(
            new TenderReviewRecord(
                tender.Id, firstSeenAt, TenderReviewStatus.Verified, assessResult.RelevanceScore,
                assessResult.Verdict, assessResult.Rationale, HandoffSentAt: null, HumanDecision: null,
                Notes: assessResult.CitedClause),
            cancellationToken);

        var handoffOutcome = await handoffStage.RunAsync(
            assessResult.TenderDetail, assessResult.Verdict, assessResult.Rationale, assessResult.RelevanceScore,
            options.HandoffValueThreshold, cancellationToken);

        await persistStage.RunAsync(
            new TenderReviewRecord(
                tender.Id, firstSeenAt, handoffOutcome.FinalStatus, assessResult.RelevanceScore,
                assessResult.Verdict, assessResult.Rationale, handoffOutcome.HandoffSentAt,
                HumanDecision: handoffOutcome.FinalStatus == TenderReviewStatus.HandedOff ? HumanDecisionStatus.Pending : null,
                Notes: assessResult.CitedClause),
            cancellationToken);

        return handoffOutcome.FinalStatus;
    }
}
