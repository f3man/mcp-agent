using System.Text.Json.Serialization;

namespace LoopOrchestrator.Mcp;

/// <summary>
/// Deserialization target for the JSON returned by McpServer's list_tenders/get_tender/
/// search_tenders tools. Deliberately duplicated from McpServer/Tenders/TenderDtos.cs rather than
/// shared via a common project — the wire shape is a protocol boundary (MCP JSON-RPC), not a
/// shared-code boundary, matching how McpServer itself never shares its internal Prozorro DTOs
/// with its own public schema. If these drift from the server's, that's a signal the two sides
/// need to react independently to a wire-contract change.
/// </summary>
public sealed record MoneyAmount(
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("currency")] string Currency);

public sealed record ProcuringEntityInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("region")] string? Region);

public sealed record TenderPeriodInfo(
    [property: JsonPropertyName("startDate")] DateTimeOffset? StartDate,
    [property: JsonPropertyName("endDate")] DateTimeOffset? EndDate);

public sealed record TenderSummary(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("titleEn")] string? TitleEn,
    [property: JsonPropertyName("cpvCategory")] string? CpvCategory,
    [property: JsonPropertyName("value")] MoneyAmount? Value,
    [property: JsonPropertyName("procuringEntity")] ProcuringEntityInfo ProcuringEntity,
    [property: JsonPropertyName("tenderPeriod")] TenderPeriodInfo TenderPeriod,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("sourceUrl")] string SourceUrl);

public sealed record TenderDetail(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("titleEn")] string? TitleEn,
    [property: JsonPropertyName("cpvCategory")] string? CpvCategory,
    [property: JsonPropertyName("value")] MoneyAmount? Value,
    [property: JsonPropertyName("procuringEntity")] ProcuringEntityInfo ProcuringEntity,
    [property: JsonPropertyName("tenderPeriod")] TenderPeriodInfo TenderPeriod,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("sourceUrl")] string SourceUrl,
    [property: JsonPropertyName("eligibilityText")] string EligibilityText);
