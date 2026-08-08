using System.Text.Json.Serialization;

namespace McpServer.Tenders;

/// <summary>
/// The public, simplified schema returned by every tender-related tool. This is deliberately
/// decoupled from the upstream Prozorro/OCDS shape (see Prozorro/ProzorroDtos.cs) — upstream
/// fields never leak into tool responses. JSON property names are pinned explicitly so the
/// wire shape matches docs/01-mcp-server.md regardless of the SDK's default naming policy.
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
