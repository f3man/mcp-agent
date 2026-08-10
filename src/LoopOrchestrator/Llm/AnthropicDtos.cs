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

// Id/Name/Input are populated only when Type == "tool_use" — Claude pausing mid-turn to request
// a tool call instead of (or alongside) finishing its answer. See AnthropicClient.RunAgenticToolLoopAsync.
public sealed record AnthropicContentBlock(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("id")] string? Id = null,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("input")] JsonElement? Input = null);

/// <summary>One entry in a `tools` array — matches Anthropic's tools[] shape exactly
/// (name/description/input_schema). MCP's own Tool.InputSchema is already JSON Schema, so mapping
/// an MCP tool descriptor into this is a direct 1:1 copy, no translation needed.</summary>
public sealed record AnthropicTool(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("input_schema")] JsonElement InputSchema);

/// <summary>Request shape used only by RunAgenticToolLoopAsync's multi-turn conversation — kept
/// separate from AnthropicMessageRequest (whose Messages/Content are plain strings, used by every
/// existing single-shot caller) because a tool-use turn's content is a list of content-block
/// objects (text / tool_use echoed back / tool_result), not a bare string. Content entries are
/// built as plain anonymous objects at the call site — same "anonymous object for JSON building"
/// convention already used in Loop/Stages/HandoffStage.cs's BuildBlocks.</summary>
public sealed record AnthropicAgenticRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("max_tokens")] int MaxTokens,
    [property: JsonPropertyName("system")] string System,
    [property: JsonPropertyName("messages")] IReadOnlyList<object> Messages,
    [property: JsonPropertyName("tools")] IReadOnlyList<AnthropicTool> Tools);

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
