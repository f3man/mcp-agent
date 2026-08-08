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
            : t.Description ?? string.Empty);

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
}
