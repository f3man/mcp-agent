using System.Text.Json.Serialization;

namespace McpServer.Tenders.Prozorro;

/// <summary>
/// Raw upstream JSON shapes from the Prozorro public API (https://public.api.openprocurement.org).
/// These are `internal` on purpose — never returned directly by a tool; ProzorroMapper.cs maps
/// them onto the public schema in TenderDtos.cs. Only the subset of fields this server actually
/// uses is modeled here.
///
/// NB: the upstream list endpoint (`GET /tenders`) returns only { id, dateModified } per item —
/// full tender data requires a follow-up `GET /tenders/{id}` per id.
/// </summary>
internal sealed record ProzorroListItem(
    string Id,
    DateTimeOffset DateModified);

internal sealed record ProzorroPageLink(
    [property: JsonPropertyName("offset")] string? Offset);

internal sealed record ProzorroListResponse(
    List<ProzorroListItem> Data,
    [property: JsonPropertyName("next_page")] ProzorroPageLink? NextPage);

internal sealed record ProzorroValue(
    decimal Amount,
    string Currency);

internal sealed record ProzorroAddress(
    string? Region);

internal sealed record ProzorroProcuringEntity(
    string Name,
    ProzorroAddress? Address);

internal sealed record ProzorroPeriod(
    DateTimeOffset? StartDate,
    DateTimeOffset? EndDate);

internal sealed record ProzorroClassification(
    string? Id,
    string? Description);

internal sealed record ProzorroItem(
    ProzorroClassification? Classification);

internal sealed record ProzorroTender(
    string Id,
    // NB: the real upstream field is snake_case ("title_en") even though every other field on
    // this record is camelCase — a genuine gotcha confirmed against the live API.
    string Title,
    [property: JsonPropertyName("title_en")] string? TitleEn,
    string? Description,
    // The spec's eligibilityText maps from this field, but many real tenders (confirmed on a
    // live belowThreshold sample) leave it empty — ProzorroMapper falls back to Description.
    string? EligibilityCriteria,
    ProzorroValue? Value,
    ProzorroProcuringEntity? ProcuringEntity,
    ProzorroPeriod? TenderPeriod,
    string Status,
    List<ProzorroItem>? Items,
    // The human-readable tender number shown on the public portal (e.g. UA-2024-...) — distinct
    // from the internal `id` (a long hex string) used for API lookups. Used to build sourceUrl.
    [property: JsonPropertyName("tenderID")] string? TenderNumber);

internal sealed record ProzorroTenderResponse(ProzorroTender? Data);
