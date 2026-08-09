using System.Text.Json;
using System.Text.Json.Serialization;
using LoopOrchestrator.Llm;
using LoopOrchestrator.Mcp;
using LoopOrchestrator.Rag;
using LoopOrchestrator.Telemetry;

namespace LoopOrchestrator.Loop.Stages;

public sealed record VerifyResult(string Verdict, string Rationale, string? CitedClause, TenderDetail TenderDetail);

/// <summary>Stage 3 — Verify (RAG). Fetches full tender detail, retrieves qualification snippets,
/// one LLM call using the verifier prompt. Enforces the "eligible/ineligible must cite a clause"
/// guardrail in code as well as in the prompt, forcing "uncertain" if the model doesn't
/// comply.</summary>
public sealed class VerifyStage(
    IMcpTenderClient mcpClient, IEligibilityIndex eligibilityIndex, AnthropicClient anthropicClient, ILogger<VerifyStage> logger)
{
    private const int MaxTokens = 1024;
    private const int TopK = 5;

    public Task<VerifyResult> RunAsync(string tenderId, CancellationToken cancellationToken) =>
        LoopTelemetry.TraceStageAsync("verify", tenderId, async () =>
        {
            var detail = await mcpClient.GetTenderAsync(tenderId, cancellationToken);

            // A blank eligibilityText is a real, observed case (not every Prozorro tender documents
            // one) — querying the index with empty input would throw at the embedding-API layer
            // (confirmed live: OpenAI.EmbeddingClient rejects an empty string outright), and even if
            // it didn't, there's nothing meaningful to search for. Same "uncertain" fallback as the
            // zero-snippets-returned case below, just skipping the doomed API call.
            if (string.IsNullOrWhiteSpace(detail.EligibilityText))
            {
                logger.LogWarning(
                    "Tender {TenderId} has no eligibilityText — verdict forced to uncertain without querying the index.",
                    tenderId);
                return new VerifyResult(
                    "uncertain",
                    "Tender has no eligibilityText to verify against — cannot confirm eligibility without human review.",
                    CitedClause: null, detail);
            }

            var snippets = await eligibilityIndex.QueryAsync(detail.EligibilityText, TopK, cancellationToken);
            if (snippets.Count == 0)
            {
                logger.LogWarning(
                    "No eligibility snippets retrieved for tender {TenderId} (index empty/unavailable) — verdict forced to uncertain.",
                    tenderId);
                return new VerifyResult(
                    "uncertain",
                    "Eligibility index unavailable or returned no relevant qualification snippets — cannot confirm eligibility without human review.",
                    CitedClause: null, detail);
            }

            var userMessage = JsonSerializer.Serialize(new { tender = detail, qualificationSnippets = snippets.Select(s => s.Text) });

            using var llmActivity = LoopTelemetry.StartLlmCallActivity(PromptBook.VerifierVersion, userMessage);
            var result = await anthropicClient.CompleteStructuredAsync<VerifierJsonResult>(
                PromptBook.VerifierSystemPrompt, userMessage, JsonSchemas.EligibilityVerdict, MaxTokens, cancellationToken);
            LoopTelemetry.SetLlmOutput(llmActivity, JsonSerializer.Serialize(result));

            if (result.Verdict is "eligible" or "ineligible" && string.IsNullOrWhiteSpace(result.CitedClause))
            {
                logger.LogWarning(
                    "Verifier returned verdict '{Verdict}' without a citedClause for tender {TenderId} — forcing uncertain.",
                    result.Verdict, tenderId);
                return new VerifyResult(
                    "uncertain", $"Verifier omitted a required citedClause for verdict '{result.Verdict}'.", CitedClause: null, detail);
            }

            return new VerifyResult(result.Verdict, result.Rationale, result.CitedClause, detail);
        });

    private sealed record VerifierJsonResult(
        [property: JsonPropertyName("verdict")] string Verdict,
        [property: JsonPropertyName("rationale")] string Rationale,
        [property: JsonPropertyName("citedClause")] string? CitedClause);
}
