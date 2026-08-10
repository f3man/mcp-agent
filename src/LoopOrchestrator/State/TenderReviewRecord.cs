namespace LoopOrchestrator.State;

/// <summary>
/// One tender's review lifecycle, verbatim from docs/task-2/01-loop-orchestrator.md Stage 4.
/// This is what makes reruns idempotent — Stage 1 (Discover) excludes any tender already present
/// here (see ITenderStateStore.GetSeenTenderIdsAsync).
/// </summary>
public sealed record TenderReviewRecord(
    string TenderId,
    DateTimeOffset FirstSeenAt,
    string Status,              // New | Classified | Verified | HandedOff | Skipped
    double? RelevanceScore,
    string? EligibilityVerdict,
    string? EligibilityRationale,
    DateTimeOffset? HandoffSentAt,
    string? HumanDecision,      // Pending | Bid | NoBid
    string? Notes,
    // Trailing optional — added for the hill-climbing/self-improvement loop (see Analysis/) so
    // every existing positional call site keeps compiling unchanged. HumanDecision was previously
    // written once as "Pending" and never updated again; these two fields are what actually get
    // set when a human clicks a decision link from the Slack brief (see HandoffStage/Program.cs).
    DateTimeOffset? HumanDecidedAt = null,
    string? HumanDecisionNote = null);

/// <summary>Status values a TenderReviewRecord can hold — kept as constants rather than an enum
/// since the state store persists them as plain strings (Azure Table Storage has no enum type).</summary>
public static class TenderReviewStatus
{
    public const string New = "New";
    public const string Classified = "Classified";
    public const string Verified = "Verified";
    public const string HandedOff = "HandedOff";
    public const string Skipped = "Skipped";
}

public static class HumanDecisionStatus
{
    public const string Pending = "Pending";
    public const string Bid = "Bid";
    public const string NoBid = "NoBid";
}
