using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LoopOrchestrator.Llm;
using Microsoft.Extensions.Logging.Abstractions;

namespace LoopOrchestrator.Tests;

/// <summary>
/// AnthropicClient.RunAgenticToolLoopAsync's loop mechanics — same "fake HttpMessageHandler
/// returning canned Anthropic-shaped JSON" approach already established in
/// StructuredJsonRetryTests.cs, extended with tool_use response sequences.
/// </summary>
public class AgenticToolLoopTests
{
    private static readonly JsonElement EmptySchema = JsonDocument.Parse("{}").RootElement;

    [Fact]
    public async Task StopsAtEndTurn_WhenNoToolUseRequested()
    {
        var handler = new RecordingHandler(_ => AnthropicTextResponse("nothing to look up, all done"));
        var client = CreateClient(handler);

        var result = await client.RunAgenticToolLoopAsync(
            "system", "user", tools: [], executeToolAsync: (_, _, _) => throw new InvalidOperationException("should never be called"),
            maxTokens: 100, maxIterations: 5, CancellationToken.None);

        Assert.Equal("nothing to look up, all done", result.FinalText);
        Assert.Empty(result.ToolCallsMade);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task CallsToolThenFinishes_OnToolUseThenEndTurn()
    {
        var handler = new RecordingHandler(
            _ => AnthropicToolUseResponse("toolu_1", "get_tender", """{"tenderId":"abc123"}"""),
            _ => AnthropicTextResponse("tender abc123 looks relevant"));
        var client = CreateClient(handler);

        string? calledToolName = null;
        JsonElement calledInput = default;
        var result = await client.RunAgenticToolLoopAsync(
            "system", "user", tools: [new AnthropicTool("get_tender", "desc", EmptySchema)],
            executeToolAsync: (name, input, _) =>
            {
                calledToolName = name;
                calledInput = input;
                return Task.FromResult("""{"id":"abc123","title":"Road repair"}""");
            },
            maxTokens: 100, maxIterations: 5, CancellationToken.None);

        Assert.Equal("tender abc123 looks relevant", result.FinalText);
        Assert.Equal(2, handler.CallCount);
        Assert.Equal("get_tender", calledToolName);
        Assert.Equal("abc123", calledInput.GetProperty("tenderId").GetString());

        Assert.Single(result.ToolCallsMade);
        Assert.Equal("get_tender", result.ToolCallsMade[0].ToolName);
        Assert.Contains("Road repair", result.ToolCallsMade[0].Result);

        // Second request must carry the tool_result back to Anthropic, tagged with the same
        // tool_use id the first response handed out.
        var secondRequestBody = handler.RequestBodies[1];
        Assert.Contains("toolu_1", secondRequestBody);
        Assert.Contains("tool_result", secondRequestBody);
        Assert.Contains("Road repair", secondRequestBody);
    }

    [Fact]
    public async Task MultipleToolCalls_AccumulateAcrossIterations()
    {
        var handler = new RecordingHandler(
            _ => AnthropicToolUseResponse("toolu_1", "search_tenders", """{"keywords":"road"}"""),
            _ => AnthropicToolUseResponse("toolu_2", "get_tender", """{"tenderId":"xyz"}"""),
            _ => AnthropicTextResponse("done after two lookups"));
        var client = CreateClient(handler);

        var calls = new List<string>();
        var result = await client.RunAgenticToolLoopAsync(
            "system", "user",
            tools: [new AnthropicTool("search_tenders", "d", EmptySchema), new AnthropicTool("get_tender", "d", EmptySchema)],
            executeToolAsync: (name, _, _) => { calls.Add(name); return Task.FromResult("ok"); },
            maxTokens: 100, maxIterations: 5, CancellationToken.None);

        Assert.Equal("done after two lookups", result.FinalText);
        Assert.Equal(3, handler.CallCount);
        Assert.Equal(["search_tenders", "get_tender"], calls);
        Assert.Equal(2, result.ToolCallsMade.Count);
    }

    [Fact]
    public async Task MaxIterationsExceeded_StopsAndReturnsAccumulatedCalls_InsteadOfLoopingForever()
    {
        // Claude keeps requesting the same tool forever — the cap must actually stop the loop.
        var handler = new RecordingHandler(_ => AnthropicToolUseResponse("toolu_x", "get_tender", "{}"));
        var client = CreateClient(handler);

        var result = await client.RunAgenticToolLoopAsync(
            "system", "user", tools: [new AnthropicTool("get_tender", "d", EmptySchema)],
            executeToolAsync: (_, _, _) => Task.FromResult("ok"),
            maxTokens: 100, maxIterations: 3, CancellationToken.None);

        Assert.Equal(string.Empty, result.FinalText);
        Assert.Equal(3, result.ToolCallsMade.Count);
        Assert.Equal(3, handler.CallCount); // never a 4th request past the cap
    }

    private static AnthropicClient CreateClient(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.anthropic.test/") };
        return new AnthropicClient(httpClient, NullLogger<AnthropicClient>.Instance);
    }

    private static HttpResponseMessage AnthropicTextResponse(string text) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new
        {
            id = "msg_1",
            type = "message",
            role = "assistant",
            model = "claude-haiku-4-5-20251001",
            content = new[] { new { type = "text", text } },
            stop_reason = "end_turn",
            usage = new { input_tokens = 10, output_tokens = 10 },
        }),
    };

    private static HttpResponseMessage AnthropicToolUseResponse(string toolUseId, string toolName, string inputJson) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new
        {
            id = "msg_tool",
            type = "message",
            role = "assistant",
            model = "claude-haiku-4-5-20251001",
            content = new object[]
            {
                new { type = "tool_use", id = toolUseId, name = toolName, input = JsonDocument.Parse(inputJson).RootElement },
            },
            stop_reason = "tool_use",
            usage = new { input_tokens = 10, output_tokens = 10 },
        }),
    };

    private sealed class RecordingHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responders) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            var responder = responders[Math.Min(CallCount, responders.Length - 1)];
            CallCount++;
            return responder(request);
        }
    }
}
