using System.Text.Json;
using System.Text.Json.Serialization;

namespace LoopOrchestrator.Llm;

public sealed record AnthropicMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

public sealed record AnthropicJsonFormat(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("schema")] JsonElement Schema);

public sealed record AnthropicOutputConfig(
    [property: JsonPropertyName("format")] AnthropicJsonFormat Format);

public sealed record AnthropicMessageRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("max_tokens")] int MaxTokens,
    [property: JsonPropertyName("system")] string System,
    [property: JsonPropertyName("messages")] IReadOnlyList<AnthropicMessage> Messages,
    // WhenWritingNull: the plain-text path (Stage 5 — handoff summarizer) passes outputConfig:
    // null, and the default System.Text.Json behavior serializes that as a literal
    // `"output_config": null` — confirmed live that Anthropic's API rejects that outright
    // ("Input does not match the expected shape"), so the field must be omitted entirely rather
    // than sent as null.
    [property: JsonPropertyName("output_config")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    AnthropicOutputConfig? OutputConfig = null);

public sealed record AnthropicContentBlock(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string? Text);

public sealed record AnthropicUsage(
    [property: JsonPropertyName("input_tokens")] int InputTokens,
    [property: JsonPropertyName("output_tokens")] int OutputTokens);

public sealed record AnthropicMessageResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("content")] IReadOnlyList<AnthropicContentBlock> Content,
    [property: JsonPropertyName("stop_reason")] string? StopReason,
    [property: JsonPropertyName("usage")] AnthropicUsage? Usage);

public sealed record AnthropicErrorBody(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("message")] string? Message);

public sealed record AnthropicErrorResponse(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("error")] AnthropicErrorBody Error);
