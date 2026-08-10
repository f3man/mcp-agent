using System.Net.Http.Json;
using System.Text.Json;

namespace LoopOrchestrator.Llm;

/// <summary>
/// Hand-rolled Anthropic Messages API client — no Anthropic SDK dependency, matching this
/// codebase's existing style (McpServer/Tenders/ProzorroClient.cs also hand-rolls its upstream
/// client rather than pulling in a vendor SDK). BaseAddress ("https://api.anthropic.com/") and the
/// x-api-key/anthropic-version default headers are configured on the injected HttpClient in
/// Program.cs, the same pattern ProzorroClient uses for its own upstream call.
/// </summary>
public sealed class AnthropicClient(HttpClient httpClient, ILogger<AnthropicClient> logger)
{
    // Dated model id per Anthropic's current naming convention for Claude Haiku 4.5 — fast/cheap,
    // appropriate for a high-volume, low-complexity classify/verify/summarize workload per tender.
    private const string Model = "claude-haiku-4-5-20251001";

    /// <summary>One structured-output call (Stages 2/3 — classify, verify). Returns the raw JSON
    /// text from the response's first text content block — callers that need typed results should
    /// go through CompleteStructuredAsync instead, which adds the retry-on-parse-failure loop.</summary>
    public Task<string> CompleteJsonAsync(
        string systemPrompt, string userMessage, JsonElement schema, int maxTokens, CancellationToken cancellationToken) =>
        SendAsync(systemPrompt, userMessage, new AnthropicOutputConfig(new AnthropicJsonFormat("json_schema", schema)), maxTokens, cancellationToken);

    /// <summary>Plain-text completion, no output_config (Stage 5 — handoff summarizer). The prompt
    /// book requires plain text output for this stage specifically ("no JSON, no markdown headers
    /// — this goes straight into a Slack message"), so forcing a JSON schema here would contradict
    /// the prompt's own instructions.</summary>
    public Task<string> CompletePlainTextAsync(
        string systemPrompt, string userMessage, int maxTokens, CancellationToken cancellationToken) =>
        SendAsync(systemPrompt, userMessage, outputConfig: null, maxTokens, cancellationToken);

    /// <summary>Calls CompleteJsonAsync and deserializes the result as T, retrying the whole LLM
    /// call (not just the parse) on failure — defense-in-depth per the prompt book's guardrail;
    /// with output_config.format already schema-guaranteeing the shape, a parse failure here
    /// should be rare (a network hiccup returning a truncated body, etc.), not the common case.</summary>
    public async Task<T> CompleteStructuredAsync<T>(
        string systemPrompt, string userMessage, JsonElement schema, int maxTokens,
        CancellationToken cancellationToken, int maxAttempts = 3)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var text = await CompleteJsonAsync(systemPrompt, userMessage, schema, maxTokens, cancellationToken);
            try
            {
                return JsonSerializer.Deserialize<T>(text) ?? throw new JsonException("Deserialized to null.");
            }
            catch (JsonException ex)
            {
                lastError = ex;
                logger.LogWarning(ex,
                    "LLM structured-output parse failed on attempt {Attempt}/{MaxAttempts}. Raw text: {Text}",
                    attempt, maxAttempts, text);
            }
        }

        throw new InvalidOperationException($"LLM failed to produce parseable JSON after {maxAttempts} attempts.", lastError);
    }

    /// <summary>Real agentic tool use: Claude decides itself whether/how to call any of `tools`,
    /// as many times as it needs (up to `maxIterations`), before producing a final plain-text
    /// answer. Deliberately NOT combined with output_config.format/structured output in the same
    /// call — this loop's job is the research phase; callers make a normal, separate
    /// CompleteStructuredAsync call afterward (with this loop's findings folded into that call's
    /// user message) to get a schema-guaranteed final verdict. Keeping the two mechanisms in
    /// separate calls reuses CompleteStructuredAsync's existing, already-tested retry/schema
    /// machinery unchanged, rather than depending on whether Anthropic's API cleanly supports
    /// mixing tool-use and output_config in one request.</summary>
    public async Task<AgenticResult> RunAgenticToolLoopAsync(
        string systemPrompt, string initialUserMessage, IReadOnlyList<AnthropicTool> tools,
        Func<string, JsonElement, CancellationToken, Task<string>> executeToolAsync,
        int maxTokens, int maxIterations, CancellationToken cancellationToken)
    {
        var messages = new List<object> { new { role = "user", content = initialUserMessage } };
        var toolCallsMade = new List<ToolCallRecord>();

        for (var iteration = 1; iteration <= maxIterations; iteration++)
        {
            var request = new AnthropicAgenticRequest(Model, maxTokens, systemPrompt, messages, tools);
            using var response = await httpClient.PostAsJsonAsync("v1/messages", request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("Anthropic API returned {StatusCode}: {Body}", (int)response.StatusCode, body);
                throw new InvalidOperationException($"Anthropic API returned {(int)response.StatusCode}: {body}");
            }

            var payload = JsonSerializer.Deserialize<AnthropicMessageResponse>(body)
                ?? throw new InvalidOperationException("Anthropic API returned an empty response body.");

            var toolUseBlocks = payload.Content.Where(b => b.Type == "tool_use").ToList();
            if (toolUseBlocks.Count == 0 || payload.StopReason != "tool_use")
            {
                var finalText = payload.Content.FirstOrDefault(b => b.Type == "text")?.Text ?? string.Empty;
                return new AgenticResult(finalText, toolCallsMade);
            }

            // Echo the assistant's own turn back verbatim (Anthropic requires the tool_use blocks
            // to appear in the conversation history before the matching tool_result), then one
            // tool_result per tool_use, in the same order, in a single following user turn.
            messages.Add(new
            {
                role = "assistant",
                content = payload.Content.Select(b => b.Type == "tool_use"
                    ? (object)new { type = "tool_use", id = b.Id, name = b.Name, input = b.Input }
                    : new { type = "text", text = b.Text }),
            });

            var toolResults = new List<object>();
            foreach (var block in toolUseBlocks)
            {
                var input = block.Input ?? JsonDocument.Parse("{}").RootElement;
                var resultText = await executeToolAsync(block.Name!, input, cancellationToken);
                toolCallsMade.Add(new ToolCallRecord(block.Name!, input, resultText));
                toolResults.Add(new { type = "tool_result", tool_use_id = block.Id, content = resultText });
            }
            messages.Add(new { role = "user", content = toolResults });
        }

        logger.LogWarning(
            "Agentic tool loop hit maxIterations={MaxIterations} without Claude finishing — " +
            "returning what was accumulated so far rather than looping forever.", maxIterations);
        return new AgenticResult(string.Empty, toolCallsMade);
    }

    private async Task<string> SendAsync(
        string systemPrompt, string userMessage, AnthropicOutputConfig? outputConfig, int maxTokens, CancellationToken cancellationToken)
    {
        var request = new AnthropicMessageRequest(
            Model: Model,
            MaxTokens: maxTokens,
            System: systemPrompt,
            Messages: [new AnthropicMessage("user", userMessage)],
            OutputConfig: outputConfig);

        using var response = await httpClient.PostAsJsonAsync("v1/messages", request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Anthropic API returned {StatusCode}: {Body}", (int)response.StatusCode, body);
            throw new InvalidOperationException($"Anthropic API returned {(int)response.StatusCode}: {body}");
        }

        var payload = JsonSerializer.Deserialize<AnthropicMessageResponse>(body)
            ?? throw new InvalidOperationException("Anthropic API returned an empty response body.");

        var text = payload.Content.FirstOrDefault(b => b.Type == "text")?.Text;
        return text ?? throw new InvalidOperationException("Anthropic API response had no text content block.");
    }
}

/// <summary>What a RunAgenticToolLoopAsync call actually did — FinalText is Claude's concluding
/// plain-text answer once it stopped requesting tools; ToolCallsMade is the full record of what
/// was called and with what result, folded into the caller's follow-up structured-verdict prompt
/// so the final schema-guaranteed answer can reference what was actually found.</summary>
public sealed record AgenticResult(string FinalText, IReadOnlyList<ToolCallRecord> ToolCallsMade);

public sealed record ToolCallRecord(string ToolName, JsonElement Input, string Result);
