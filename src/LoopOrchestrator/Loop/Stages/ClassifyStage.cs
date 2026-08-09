using System.Text.Json;
using System.Text.Json.Serialization;
using LoopOrchestrator.Llm;
using LoopOrchestrator.Mcp;
using LoopOrchestrator.Telemetry;

namespace LoopOrchestrator.Loop.Stages;

public sealed record ClassifyResult(bool Relevant, double RelevanceScore, string Reason);

/// <summary>Stage 2 — Classify. One LLM call per new tender using the triage prompt.</summary>
public sealed class ClassifyStage(AnthropicClient anthropicClient)
{
    private const int MaxTokens = 512;

    public Task<ClassifyResult> RunAsync(TenderSummary tender, CompanyProfileData companyProfile, CancellationToken cancellationToken) =>
        LoopTelemetry.TraceStageAsync("classify", tender.Id, async () =>
        {
            var userMessage = JsonSerializer.Serialize(new { tender, companyProfile });

            using var llmActivity = LoopTelemetry.StartLlmCallActivity(PromptBook.TriageVersion, userMessage);
            var result = await anthropicClient.CompleteStructuredAsync<TriageJsonResult>(
                PromptBook.TriageSystemPrompt, userMessage, JsonSchemas.TriageResult, MaxTokens, cancellationToken);
            LoopTelemetry.SetLlmOutput(llmActivity, JsonSerializer.Serialize(result));

            return new ClassifyResult(result.Relevant, result.RelevanceScore, result.Reason);
        });

    private sealed record TriageJsonResult(
        [property: JsonPropertyName("relevant")] bool Relevant,
        [property: JsonPropertyName("relevanceScore")] double RelevanceScore,
        [property: JsonPropertyName("reason")] string Reason);
}
