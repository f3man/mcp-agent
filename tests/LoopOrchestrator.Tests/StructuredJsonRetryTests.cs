using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LoopOrchestrator.Llm;
using Microsoft.Extensions.Logging.Abstractions;

namespace LoopOrchestrator.Tests;

public class StructuredJsonRetryTests
{
    private sealed record Sample(bool Flag);

    [Fact]
    public async Task CompleteStructuredAsync_RetriesOnParseFailure_ThenSucceeds()
    {
        var handler = new SequencedHandler(
            _ => AnthropicTextResponse("this is not json"),
            _ => AnthropicTextResponse("still not json"),
            _ => AnthropicTextResponse("""{"Flag":true}"""));
        var client = CreateClient(handler);

        var result = await client.CompleteStructuredAsync<Sample>(
            "system", "user", JsonDocument.Parse("{}").RootElement, 100, CancellationToken.None, maxAttempts: 3);

        Assert.True(result.Flag);
        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task CompleteStructuredAsync_ThrowsAfterMaxAttempts_WhenAlwaysMalformed()
    {
        var handler = new SequencedHandler(_ => AnthropicTextResponse("never valid json"));
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CompleteStructuredAsync<Sample>(
                "system", "user", JsonDocument.Parse("{}").RootElement, 100, CancellationToken.None, maxAttempts: 2));

        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task CompleteStructuredAsync_SucceedsOnFirstAttempt_WhenAlreadyValid()
    {
        var handler = new SequencedHandler(_ => AnthropicTextResponse("""{"Flag":false}"""));
        var client = CreateClient(handler);

        var result = await client.CompleteStructuredAsync<Sample>(
            "system", "user", JsonDocument.Parse("{}").RootElement, 100, CancellationToken.None);

        Assert.False(result.Flag);
        Assert.Equal(1, handler.CallCount);
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

    private sealed class SequencedHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responders) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var responder = responders[Math.Min(CallCount, responders.Length - 1)];
            CallCount++;
            return Task.FromResult(responder(request));
        }
    }
}
