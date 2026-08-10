using System.Text.Json;
using System.Text.Json.Serialization;
using LoopOrchestrator.Llm;
using LoopOrchestrator.Notifications;
using LoopOrchestrator.State;
using LoopOrchestrator.Telemetry;

namespace LoopOrchestrator.Analysis;

public sealed record AnalysisResult(bool Started, bool Proposed, string? Reason);

/// <summary>
/// The self-improvement ("hill-climbing") outer loop, triggered on a much slower cadence than the
/// main loop (Analysis/AnalysisBackgroundWorker.cs) and on demand (POST /analyze-now). Reads
/// resolved handoffs (a human actually clicked Bid/NoBid — see Loop/Stages/HandoffStage.cs and
/// Program.cs's /decisions endpoints), looks for disagreements between the system's own verdict
/// and what the human decided, and — only if there's enough signal — asks the LLM to propose a
/// prompt revision.
///
/// This class never writes to PromptBook.cs or any file. It only ever produces a
/// PromptProposalRecord and a Slack message; see that record's doc comment for why. The one thing
/// it DOES enforce automatically is PromptGuardrails.IsSafe — a proposal that would strip a
/// required safety phrase from its target prompt is rejected before a human ever sees it.
/// </summary>
public sealed class AnalysisRunner(
    ITenderStateStore stateStore,
    AnthropicClient anthropicClient,
    SlackNotifier slackNotifier,
    AnalysisOptions options,
    ILogger<AnalysisRunner> logger)
{
    private const int MaxTokens = 2048;

    // Process-wide "only one analysis at a time" guard — same reasoning as
    // Loop/LoopRunner.cs's static RunGate (this class is Scoped, a new instance per run).
    private static readonly SemaphoreSlim RunGate = new(1, 1);

    public async Task<AnalysisResult> TryRunOnceAsync(CancellationToken cancellationToken)
    {
        if (!await RunGate.WaitAsync(0, cancellationToken))
        {
            logger.LogInformation("An analysis run is already in progress — skipping this trigger.");
            return new AnalysisResult(Started: false, Proposed: false, Reason: "already running");
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

    private async Task<AnalysisResult> RunAsync(CancellationToken cancellationToken)
    {
        using var runActivity = LoopTelemetry.StartAnalysisRunActivity();

        var since = DateTimeOffset.UtcNow.AddDays(-options.AnalysisLookbackDays);
        var recent = await stateStore.GetRecentAsync(since, cancellationToken);

        // "Resolved" — a human actually clicked a decision link, not just "we handed it off."
        var resolved = recent
            .Where(r => r.Status == TenderReviewStatus.HandedOff
                && (r.HumanDecision == HumanDecisionStatus.Bid || r.HumanDecision == HumanDecisionStatus.NoBid))
            .ToList();

        var disagreements = resolved.Where(IsDisagreement).ToList();

        logger.LogInformation(
            "Analysis: {ResolvedCount} resolved handoffs in the last {LookbackDays} days, {DisagreementCount} disagreements.",
            resolved.Count, options.AnalysisLookbackDays, disagreements.Count);

        // Never propose from a single anecdote — mirrors LoopRunner's MAX_TENDERS_PER_RUN
        // guardrail in spirit: a cheap, pre-LLM check that avoids burning a call (and, more
        // importantly, avoids overfitting a prompt change to noise) when there just isn't
        // enough signal yet.
        if (disagreements.Count < options.MinDisagreementsForProposal)
        {
            return new AnalysisResult(Started: true, Proposed: false, Reason: "insufficient signal");
        }

        var userMessage = JsonSerializer.Serialize(resolved.Select(r => new
        {
            tenderId = r.TenderId,
            verdict = r.EligibilityVerdict,
            rationale = r.EligibilityRationale,
            citedClause = r.Notes,
            relevanceScore = r.RelevanceScore,
            humanDecision = r.HumanDecision,
            humanDecisionNote = r.HumanDecisionNote,
        }));

        using var llmActivity = LoopTelemetry.StartLlmCallActivity(PromptBook.AnalysisVersion, userMessage);
        var result = await anthropicClient.CompleteStructuredAsync<AnalysisJsonResult>(
            PromptBook.AnalysisSystemPrompt, userMessage, JsonSchemas.PromptProposal, MaxTokens, cancellationToken);
        LoopTelemetry.SetLlmOutput(llmActivity, JsonSerializer.Serialize(result));

        var isSafe = PromptGuardrails.IsSafe(result.TargetPrompt, result.ProposedPromptText);

        var proposal = new PromptProposalRecord(
            ProposalId: Guid.NewGuid().ToString("n"),
            CreatedAt: DateTimeOffset.UtcNow,
            TargetPrompt: result.TargetPrompt,
            CurrentVersion: CurrentVersionFor(result.TargetPrompt),
            ProposedPromptText: result.ProposedPromptText,
            Justification: result.Justification,
            CitedTenderIds: result.CitedTenderIds,
            Status: isSafe ? PromptProposalStatus.Proposed : PromptProposalStatus.RejectedByGuardrail,
            SlackSentAt: null);

        await stateStore.UpsertProposalAsync(proposal, cancellationToken);

        if (!isSafe)
        {
            logger.LogWarning(
                "Analysis: proposal {ProposalId} for '{TargetPrompt}' was REJECTED by PromptGuardrails — " +
                "it dropped a required safety phrase. Persisted for audit, never sent to Slack.",
                proposal.ProposalId, result.TargetPrompt);
            return new AnalysisResult(Started: true, Proposed: false, Reason: "rejected by guardrail");
        }

        await slackNotifier.SendAsync(BuildSlackMessage(proposal), cancellationToken);

        proposal = proposal with { SlackSentAt = DateTimeOffset.UtcNow };
        await stateStore.UpsertProposalAsync(proposal, cancellationToken);

        logger.LogInformation(
            "Analysis: proposal {ProposalId} for '{TargetPrompt}' posted for human review.",
            proposal.ProposalId, proposal.TargetPrompt);
        return new AnalysisResult(Started: true, Proposed: true, Reason: null);
    }

    /// <summary>The two patterns worth flagging: the system was needlessly conservative
    /// ("uncertain" but the human went ahead and bid), or overconfident ("eligible" but the human
    /// declined). Any other combination (agreement, or "ineligible" — which never reaches a human
    /// to disagree with in the first place) isn't a disagreement by definition.</summary>
    internal static bool IsDisagreement(TenderReviewRecord record) =>
        (record.EligibilityVerdict == "uncertain" && record.HumanDecision == HumanDecisionStatus.Bid)
        || (record.EligibilityVerdict == "eligible" && record.HumanDecision == HumanDecisionStatus.NoBid);

    private static string CurrentVersionFor(string targetPrompt) => targetPrompt switch
    {
        "triage" => PromptBook.TriageVersion,
        "verifier" => PromptBook.VerifierVersion,
        "handoff" => PromptBook.HandoffVersion,
        _ => "unknown",
    };

    private static string BuildSlackMessage(PromptProposalRecord proposal) =>
        $"""
         PROMPT CHANGE PROPOSAL — needs human review (id {proposal.ProposalId})
         Target: {proposal.TargetPrompt} (currently {proposal.CurrentVersion})
         Justification: {proposal.Justification}
         Cited tenders: {string.Join(", ", proposal.CitedTenderIds)}

         Proposed replacement text:
         {proposal.ProposedPromptText}

         This was NOT applied automatically. Review at GET /proposals — if you agree, paste the
         text above into PromptBook.cs as the next version yourself.
         """;

    private sealed record AnalysisJsonResult(
        [property: JsonPropertyName("targetPrompt")] string TargetPrompt,
        [property: JsonPropertyName("proposedPromptText")] string ProposedPromptText,
        [property: JsonPropertyName("justification")] string Justification,
        [property: JsonPropertyName("citedTenderIds")] IReadOnlyList<string> CitedTenderIds);
}
