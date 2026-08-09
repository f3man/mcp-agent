using System.Text.Json.Serialization;

namespace LoopOrchestrator.Mcp;

/// <summary>Deserialization target for get_company_profile's JSON. See TenderDtos.cs for why this
/// is duplicated from McpServer rather than shared.</summary>
public sealed record PastContract(
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("year")] int Year,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("reason")] string? Reason = null);

public sealed record CompanyProfileData(
    [property: JsonPropertyName("companyName")] string CompanyName,
    [property: JsonPropertyName("categories")] IReadOnlyList<string> Categories,
    [property: JsonPropertyName("certifications")] IReadOnlyList<string> Certifications,
    [property: JsonPropertyName("regionsServed")] IReadOnlyList<string> RegionsServed,
    [property: JsonPropertyName("minProjectValue")] decimal MinProjectValue,
    [property: JsonPropertyName("maxProjectValue")] decimal MaxProjectValue,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("pastContracts")] IReadOnlyList<PastContract> PastContracts);
