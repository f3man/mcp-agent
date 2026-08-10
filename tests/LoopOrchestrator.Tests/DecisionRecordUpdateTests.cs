using LoopOrchestrator.State;

namespace LoopOrchestrator.Tests;

/// <summary>
/// DecisionUpdater is the pure logic behind GET/POST /decisions/{tenderId}/... (Program.cs) — the
/// feedback signal the self-improvement outer loop depends on. Tested directly, without needing
/// a running app or Table Storage, same split as HandoffPolicy vs HandoffStage.
/// </summary>
public class DecisionRecordUpdateTests
{
    [Theory]
    [InlineData("Bid", HumanDecisionStatus.Bid)]
    [InlineData("bid", HumanDecisionStatus.Bid)]
    [InlineData("BID", HumanDecisionStatus.Bid)]
    [InlineData("NoBid", HumanDecisionStatus.NoBid)]
    [InlineData("nobid", HumanDecisionStatus.NoBid)]
    [InlineData("no-bid", HumanDecisionStatus.NoBid)]
    [InlineData("no_bid", HumanDecisionStatus.NoBid)]
    public void ParseCanonicalDecision_AcceptsKnownVariants(string raw, string expectedCanonical)
    {
        Assert.Equal(expectedCanonical, DecisionUpdater.ParseCanonicalDecision(raw));
    }

    [Theory]
    [InlineData("maybe")]
    [InlineData("")]
    [InlineData("bidnow")]
    public void ParseCanonicalDecision_RejectsUnrecognizedInput(string raw)
    {
        Assert.Null(DecisionUpdater.ParseCanonicalDecision(raw));
    }

    [Fact]
    public void ApplyDecision_SetsDecisionAndTimestamp_PreservesOtherFields()
    {
        var existing = Record();
        var decidedAt = DateTimeOffset.UtcNow;

        var updated = DecisionUpdater.ApplyDecision(existing, HumanDecisionStatus.Bid, note: null, decidedAt);

        Assert.Equal(HumanDecisionStatus.Bid, updated.HumanDecision);
        Assert.Equal(decidedAt, updated.HumanDecidedAt);
        // Everything untouched by the decision stays exactly as it was — this is a targeted
        // update, not a full replace built from scratch.
        Assert.Equal(existing.TenderId, updated.TenderId);
        Assert.Equal(existing.Status, updated.Status);
        Assert.Equal(existing.EligibilityVerdict, updated.EligibilityVerdict);
        Assert.Equal(existing.Notes, updated.Notes);
    }

    [Fact]
    public void ApplyDecision_WithNote_SetsHumanDecisionNote()
    {
        var updated = DecisionUpdater.ApplyDecision(Record(), HumanDecisionStatus.NoBid, "too far from our region", DateTimeOffset.UtcNow);
        Assert.Equal("too far from our region", updated.HumanDecisionNote);
    }

    [Fact]
    public void ApplyDecision_WithoutNote_PreservesAnyExistingNote()
    {
        var existing = Record() with { HumanDecisionNote = "earlier note" };
        var updated = DecisionUpdater.ApplyDecision(existing, HumanDecisionStatus.Bid, note: null, DateTimeOffset.UtcNow);
        Assert.Equal("earlier note", updated.HumanDecisionNote);
    }

    private static TenderReviewRecord Record() => new(
        TenderId: "tender-1",
        FirstSeenAt: DateTimeOffset.UtcNow,
        Status: TenderReviewStatus.HandedOff,
        RelevanceScore: 0.9,
        EligibilityVerdict: "uncertain",
        EligibilityRationale: "some rationale",
        HandoffSentAt: DateTimeOffset.UtcNow,
        HumanDecision: HumanDecisionStatus.Pending,
        Notes: "some cited clause");
}
