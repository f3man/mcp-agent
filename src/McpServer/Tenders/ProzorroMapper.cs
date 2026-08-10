using McpServer.Tenders.Prozorro;

namespace McpServer.Tenders;

/// <summary>
/// Pure mapping functions from the raw Prozorro shape to the public schema. Kept dependency-free
/// and side-effect-free so it's trivially unit-testable (see ProzorroMapperTests.cs).
/// </summary>
internal static class ProzorroMapper
{
    // The public procurement portal's tender URLs are keyed by the human-readable tender number
    // (tenderID), not the internal id used for API lookups.
    private const string PublicPortalUrlFormat = "https://prozorro.gov.ua/tender/{0}";

    public static TenderSummary ToSummary(ProzorroTender t) => new(
        Id: t.Id,
        Title: t.Title,
        TitleEn: t.TitleEn,
        CpvCategory: ResolveCpvCategory(t),
        Value: ToMoneyAmount(t.Value),
        ProcuringEntity: ToProcuringEntityInfo(t.ProcuringEntity),
        TenderPeriod: ToTenderPeriodInfo(t.TenderPeriod),
        Status: t.Status,
        SourceUrl: ToSourceUrl(t));

    public static TenderDetail ToDetail(ProzorroTender t) => new(
        Id: t.Id,
        Title: t.Title,
        TitleEn: t.TitleEn,
        CpvCategory: ResolveCpvCategory(t),
        Value: ToMoneyAmount(t.Value),
        ProcuringEntity: ToProcuringEntityInfo(t.ProcuringEntity),
        TenderPeriod: ToTenderPeriodInfo(t.TenderPeriod),
        Status: t.Status,
        SourceUrl: ToSourceUrl(t),
        // eligibilityCriteria is the spec-correct upstream field, but it's frequently empty in
        // practice (e.g. belowThreshold procedures) — fall back to the tender description rather
        // than returning an empty string.
        EligibilityText: !string.IsNullOrWhiteSpace(t.EligibilityCriteria)
            ? t.EligibilityCriteria!
            : t.Description ?? string.Empty,
        TenderId: t.TenderNumber,
        ProcurementMethod: t.ProcurementMethod,
        MainProcurementCategory: t.MainProcurementCategory,
        // Never observed null/missing live, but List<ProzorroItem>? is nullable per this file's
        // existing defensive convention — default to [] rather than null so callers (including
        // LoopOrchestrator's HandoffStage) never need a null check.
        Items: t.Items?.Select(MapItem).ToList() ?? []);

    private static string? ResolveCpvCategory(ProzorroTender t) =>
        t.Items?.FirstOrDefault()?.Classification is { } c
            ? c.Description ?? c.Id
            : null;

    private static MoneyAmount? ToMoneyAmount(ProzorroValue? v) =>
        v is null ? null : new MoneyAmount(v.Amount, v.Currency);

    private static ProcuringEntityInfo ToProcuringEntityInfo(ProzorroProcuringEntity? entity) =>
        new(entity?.Name ?? string.Empty, entity?.Address?.Region);

    private static TenderPeriodInfo ToTenderPeriodInfo(ProzorroPeriod? period) =>
        new(period?.StartDate, period?.EndDate);

    private static string ToSourceUrl(ProzorroTender t) =>
        string.Format(PublicPortalUrlFormat, t.TenderNumber ?? t.Id);

    private static TenderItemInfo MapItem(ProzorroItem item) => new(
        // Confirmed live: every sampled item had a real "id" — but ProzorroItem models it
        // nullable defensively per this codebase's convention, so fall back to empty string
        // rather than propagating a null into a non-nullable public field.
        Id: item.Id ?? string.Empty,
        Description: item.Description,
        Unit: item.Unit is null ? null : new UnitInfo(item.Unit.Name),
        Quantity: item.Quantity,
        DeliveryAddress: item.DeliveryAddress is null
            ? null
            : new DeliveryAddressInfo(item.DeliveryAddress.Region, item.DeliveryAddress.Locality));
}
