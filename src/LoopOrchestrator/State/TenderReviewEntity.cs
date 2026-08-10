using Azure;
using Azure.Data.Tables;

namespace LoopOrchestrator.State;

/// <summary>
/// Table Storage row for a TenderReviewRecord. PartitionKey is a fixed constant — this dataset is
/// PoC-scale (McpServer's own upstream cache is bounded to ~150 recently-modified tenders), so a
/// single partition needs no sharding. RowKey is the tender id, escaped defensively since Table
/// Storage rejects '/','\','#','?' in keys and there's no contractual guarantee Prozorro never
/// emits one.
/// </summary>
public sealed class TenderReviewEntity : ITableEntity
{
    public const string PartitionKeyValue = "tender";

    public string PartitionKey { get; set; } = PartitionKeyValue;
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }
    public string Status { get; set; } = TenderReviewStatus.New;
    public double? RelevanceScore { get; set; }
    public string? EligibilityVerdict { get; set; }
    public string? EligibilityRationale { get; set; }
    public DateTimeOffset? HandoffSentAt { get; set; }
    public string? HumanDecision { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset? HumanDecidedAt { get; set; }
    public string? HumanDecisionNote { get; set; }

    /// <summary>Table Storage forbids '/','\','#','?' in keys — replace with '_' defensively.</summary>
    public static string EscapeKey(string rawId) =>
        rawId.Replace('/', '_').Replace('\\', '_').Replace('#', '_').Replace('?', '_');
}

internal static class TenderReviewMapper
{
    public static TenderReviewEntity ToEntity(TenderReviewRecord record) => new()
    {
        RowKey = TenderReviewEntity.EscapeKey(record.TenderId),
        FirstSeenAt = record.FirstSeenAt,
        Status = record.Status,
        RelevanceScore = record.RelevanceScore,
        EligibilityVerdict = record.EligibilityVerdict,
        EligibilityRationale = record.EligibilityRationale,
        HandoffSentAt = record.HandoffSentAt,
        HumanDecision = record.HumanDecision,
        Notes = record.Notes,
        HumanDecidedAt = record.HumanDecidedAt,
        HumanDecisionNote = record.HumanDecisionNote,
    };

    public static TenderReviewRecord ToRecord(TenderReviewEntity entity) => new(
        TenderId: entity.RowKey,
        FirstSeenAt: entity.FirstSeenAt,
        Status: entity.Status,
        RelevanceScore: entity.RelevanceScore,
        EligibilityVerdict: entity.EligibilityVerdict,
        EligibilityRationale: entity.EligibilityRationale,
        HandoffSentAt: entity.HandoffSentAt,
        HumanDecision: entity.HumanDecision,
        Notes: entity.Notes,
        HumanDecidedAt: entity.HumanDecidedAt,
        HumanDecisionNote: entity.HumanDecisionNote);
}
