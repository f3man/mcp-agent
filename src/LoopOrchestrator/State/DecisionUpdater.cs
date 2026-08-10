namespace LoopOrchestrator.State;

/// <summary>
/// Pure logic for turning a raw decision string (from a Slack-clicked GET link, or a POST body)
/// into the canonical HumanDecisionStatus value and the updated TenderReviewRecord — split out
/// from Program.cs's actual I/O (fetch/upsert) so it's unit-testable without a running app or a
/// Table Storage dependency. Same split as HandoffPolicy.ShouldEscalate (pure) vs
/// HandoffStage.RunAsync (I/O) in Loop/Stages/HandoffStage.cs.
/// </summary>
public static class DecisionUpdater
{
    /// <summary>Accepts case-insensitively, and a couple of separator variants for "no bid" — a
    /// human typing/clicking a link shouldn't have to get the exact casing right. Null for
    /// anything unrecognized.</summary>
    public static string? ParseCanonicalDecision(string raw) => raw.ToLowerInvariant() switch
    {
        "bid" => HumanDecisionStatus.Bid,
        "nobid" or "no-bid" or "no_bid" => HumanDecisionStatus.NoBid,
        _ => null,
    };

    public static TenderReviewRecord ApplyDecision(
        TenderReviewRecord existing, string canonicalDecision, string? note, DateTimeOffset decidedAt) =>
        existing with
        {
            HumanDecision = canonicalDecision,
            HumanDecidedAt = decidedAt,
            HumanDecisionNote = note ?? existing.HumanDecisionNote,
        };
}
