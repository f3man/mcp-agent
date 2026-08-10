using LoopOrchestrator.Analysis;
using LoopOrchestrator.State;

namespace LoopOrchestrator.Tests;

/// <summary>
/// AnalysisRunner.IsDisagreement is the pre-LLM signal-strength gate for the self-improvement
/// outer loop — only the two patterns that indicate the model's own confidence didn't match what
/// a human actually decided count; plain agreement, and "ineligible" (which never reaches a human
/// to disagree with), must not.
/// </summary>
public class DisagreementThresholdTests
{
    [Fact]
    public void Uncertain_ButHumanBid_IsDisagreement()
    {
        Assert.True(AnalysisRunner.IsDisagreement(Record(verdict: "uncertain", humanDecision: HumanDecisionStatus.Bid)));
    }

    [Fact]
    public void Eligible_ButHumanDeclined_IsDisagreement()
    {
        Assert.True(AnalysisRunner.IsDisagreement(Record(verdict: "eligible", humanDecision: HumanDecisionStatus.NoBid)));
    }

    [Fact]
    public void Uncertain_AndHumanAlsoDeclined_IsNotDisagreement()
    {
        Assert.False(AnalysisRunner.IsDisagreement(Record(verdict: "uncertain", humanDecision: HumanDecisionStatus.NoBid)));
    }

    [Fact]
    public void Eligible_AndHumanAlsoBid_IsNotDisagreement()
    {
        Assert.False(AnalysisRunner.IsDisagreement(Record(verdict: "eligible", humanDecision: HumanDecisionStatus.Bid)));
    }

    [Theory]
    [InlineData(HumanDecisionStatus.Bid)]
    [InlineData(HumanDecisionStatus.NoBid)]
    public void Ineligible_NeverCountsAsDisagreement_RegardlessOfHumanDecision(string humanDecision)
    {
        // "ineligible" never reaches a human via HandoffStage (HandoffPolicy.ShouldEscalate never
        // escalates it), so by definition it can't be a recorded disagreement either.
        Assert.False(AnalysisRunner.IsDisagreement(Record(verdict: "ineligible", humanDecision: humanDecision)));
    }

    [Fact]
    public void PendingDecision_IsNotDisagreement()
    {
        Assert.False(AnalysisRunner.IsDisagreement(Record(verdict: "uncertain", humanDecision: HumanDecisionStatus.Pending)));
    }

    private static TenderReviewRecord Record(string verdict, string humanDecision) => new(
        TenderId: "tender-1",
        FirstSeenAt: DateTimeOffset.UtcNow,
        Status: TenderReviewStatus.HandedOff,
        RelevanceScore: 0.8,
        EligibilityVerdict: verdict,
        EligibilityRationale: "some rationale",
        HandoffSentAt: DateTimeOffset.UtcNow,
        HumanDecision: humanDecision,
        Notes: "some cited clause");
}
