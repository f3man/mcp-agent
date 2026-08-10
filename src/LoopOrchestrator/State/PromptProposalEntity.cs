using System.Text.Json;
using Azure;
using Azure.Data.Tables;

namespace LoopOrchestrator.State;

/// <summary>Third entity kind in the same table as TenderReviewEntity/RunStateEntity — same
/// idiomatic "one table, a handful of unrelated entity kinds by partition key" pattern, rather
/// than provisioning a new Azure resource for what's a handful of rows at this scale.</summary>
internal sealed class PromptProposalEntity : ITableEntity
{
    public const string PartitionKeyValue = "proposal";

    public string PartitionKey { get; set; } = PartitionKeyValue;
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public string TargetPrompt { get; set; } = string.Empty;
    public string CurrentVersion { get; set; } = string.Empty;
    public string ProposedPromptText { get; set; } = string.Empty;
    public string Justification { get; set; } = string.Empty;

    /// <summary>Table Storage has no list type — stored as a JSON array string, same pattern as
    /// any other structured value this SDK can't represent natively.</summary>
    public string CitedTenderIdsJson { get; set; } = "[]";

    public string Status { get; set; } = PromptProposalStatus.Proposed;
    public DateTimeOffset? SlackSentAt { get; set; }
}

internal static class PromptProposalMapper
{
    public static PromptProposalEntity ToEntity(PromptProposalRecord record) => new()
    {
        RowKey = record.ProposalId,
        CreatedAt = record.CreatedAt,
        TargetPrompt = record.TargetPrompt,
        CurrentVersion = record.CurrentVersion,
        ProposedPromptText = record.ProposedPromptText,
        Justification = record.Justification,
        CitedTenderIdsJson = JsonSerializer.Serialize(record.CitedTenderIds),
        Status = record.Status,
        SlackSentAt = record.SlackSentAt,
    };

    public static PromptProposalRecord ToRecord(PromptProposalEntity entity) => new(
        ProposalId: entity.RowKey,
        CreatedAt: entity.CreatedAt,
        TargetPrompt: entity.TargetPrompt,
        CurrentVersion: entity.CurrentVersion,
        ProposedPromptText: entity.ProposedPromptText,
        Justification: entity.Justification,
        CitedTenderIds: JsonSerializer.Deserialize<List<string>>(entity.CitedTenderIdsJson) ?? [],
        Status: entity.Status,
        SlackSentAt: entity.SlackSentAt);
}
