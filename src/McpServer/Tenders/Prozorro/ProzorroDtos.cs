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

/// <summary>Confirmed live against the real API: an object with (at least) a "name" field —
/// e.g. {"name": "послуга", "code": "E48"}. Only Name is modeled; code/priceable-unit value
/// aren't used anywhere in this server.</summary>
internal sealed record ProzorroUnit(
    string? Name);

/// <summary>Confirmed live: the real key is "deliveryAddress" (not "address"), and the whole
/// object can be entirely null on some (older/cancelled) tenders; Locality can independently be
/// null OR an empty string even when Region is populated.</summary>
internal sealed record ProzorroDeliveryAddress(
    string? Region,
    string? Locality);

internal sealed record ProzorroItem(
    // Classification stays first (existing positional call sites, e.g. `new
    // ProzorroItem(new ProzorroClassification(...))`, rely on that); the new fields all default
    // to null so those call sites keep compiling unchanged.
    ProzorroClassification? Classification,
    string? Id = null,
    string? Description = null,
    ProzorroUnit? Unit = null,
    double? Quantity = null,
    ProzorroDeliveryAddress? DeliveryAddress = null);

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
    // from the internal `id` (a long hex string) used for API lookups. Used to build sourceUrl,
    // and (as of this change) also exposed publicly as TenderDetail.TenderId.
    [property: JsonPropertyName("tenderID")] string? TenderNumber,
    // Confirmed live: procurementMethod (e.g. "open"/"selective"/"limited") was present on every
    // sampled tender, old and new, but modeled nullable defensively per this file's existing
    // convention for every other optional upstream field. mainProcurementCategory (e.g.
    // "goods"/"services") is genuinely ABSENT ENTIRELY on legacy tender records — confirmed live
    // — so it must stay nullable, never default to empty string.
    string? ProcurementMethod = null,
    string? MainProcurementCategory = null);

internal sealed record ProzorroTenderResponse(ProzorroTender? Data);
