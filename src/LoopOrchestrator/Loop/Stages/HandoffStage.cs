using LoopOrchestrator.Llm;
using LoopOrchestrator.Mcp;
using LoopOrchestrator.Notifications;
using LoopOrchestrator.State;
using LoopOrchestrator.Telemetry;

namespace LoopOrchestrator.Loop.Stages;

/// <summary>
/// Pure, network-free escalation decision — kept separate from HandoffStage's LLM/Slack side
/// effects so it's trivially unit-testable (see HandoffThresholdTests.cs) without any fakes.
/// Value is assumed UAH throughout (matches the company profile and HANDOFF_VALUE_THRESHOLD's own
/// documented denomination); a missing tender value does not, by itself, trigger value-based
/// escalation — only verdict "uncertain" always escalates regardless of value.
/// </summary>
public static class HandoffPolicy
{
    public static bool ShouldEscalate(string verdict, MoneyAmount? value, decimal handoffValueThreshold) =>
        verdict switch
        {
            "uncertain" => true,
            "eligible" => value is not null && value.Amount > handoffValueThreshold,
            _ => false, // "ineligible" never escalates — persisted as Verified, no Slack noise.
        };
}

public sealed record HandoffOutcome(string FinalStatus, DateTimeOffset? HandoffSentAt);

/// <summary>
/// Stage 5 — Handoff. Escalates to Slack when HandoffPolicy.ShouldEscalate says so; otherwise logs
/// and skips (no notification noise for the obvious eligible-and-cheap-enough cases). Never calls
/// any tender-submission action — there isn't one exposed, and none should ever be added.
/// </summary>
public sealed class HandoffStage(AnthropicClient anthropicClient, SlackNotifier slackNotifier, ILogger<HandoffStage> logger)
{
    private const int MaxTokens = 1024;

    public Task<HandoffOutcome> RunAsync(
        TenderDetail tender, string verdict, string rationale, double relevanceScore, decimal handoffValueThreshold,
        CancellationToken cancellationToken) =>
        LoopTelemetry.TraceStageAsync("handoff", tender.Id, async () =>
        {
            if (!HandoffPolicy.ShouldEscalate(verdict, tender.Value, handoffValueThreshold))
            {
                logger.LogInformation(
                    "Tender {TenderId}: verdict={Verdict}, below handoff threshold — logged, no Slack notification.",
                    tender.Id, verdict);
                return new HandoffOutcome(TenderReviewStatus.Verified, HandoffSentAt: null);
            }

            var userMessage = BuildUserMessage(tender, verdict, rationale, relevanceScore);

            using var llmActivity = LoopTelemetry.StartLlmCallActivity(PromptBook.HandoffVersion, userMessage);
            var brief = await anthropicClient.CompletePlainTextAsync(
                PromptBook.HandoffSystemPrompt, userMessage, MaxTokens, cancellationToken);
            LoopTelemetry.SetLlmOutput(llmActivity, brief);

            await slackNotifier.SendAsync(brief, cancellationToken);

            logger.LogInformation("Tender {TenderId}: handed off to Slack (verdict={Verdict}).", tender.Id, verdict);
            return new HandoffOutcome(TenderReviewStatus.HandedOff, HandoffSentAt: DateTimeOffset.UtcNow);
        });

    private static string BuildUserMessage(TenderDetail tender, string verdict, string rationale, double relevanceScore) =>
        $"""
         Tender: {tender.Title}
         Value: {tender.Value?.Amount} {tender.Value?.Currency}
         Deadline: {tender.TenderPeriod.EndDate}
         Source: {tender.SourceUrl}
         Relevance score: {relevanceScore:0.00}
         Eligibility verdict: {verdict}
         Eligibility rationale: {rationale}
         """;
}
