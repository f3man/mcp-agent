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

public sealed record UnitInfo(
    [property: JsonPropertyName("name")] string? Name);

public sealed record DeliveryAddressInfo(
    [property: JsonPropertyName("region")] string? Region,
    [property: JsonPropertyName("locality")] string? Locality);

public sealed record TenderItemInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("unit")] UnitInfo? Unit,
    [property: JsonPropertyName("quantity")] double? Quantity,
    [property: JsonPropertyName("deliveryAddress")] DeliveryAddressInfo? DeliveryAddress);

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
    [property: JsonPropertyName("eligibilityText")] string EligibilityText,
    // Prozorro's own human-readable official tender number (e.g. "UA-2020-03-17-000090-a") —
    // distinct from Id above, which is the internal hex id used for SourceUrl. Genuinely new
    // information for a human, not a duplicate of an existing field.
    [property: JsonPropertyName("tenderId")] string? TenderId,
    [property: JsonPropertyName("procurementMethod")] string? ProcurementMethod,
    [property: JsonPropertyName("mainProcurementCategory")] string? MainProcurementCategory,
    [property: JsonPropertyName("items")] IReadOnlyList<TenderItemInfo> Items);
