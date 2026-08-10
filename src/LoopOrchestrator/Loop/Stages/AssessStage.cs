using System.Text.Json;
using System.Text.Json.Serialization;
using LoopOrchestrator.Llm;
using LoopOrchestrator.Mcp;
using LoopOrchestrator.Rag;
using LoopOrchestrator.Telemetry;

namespace LoopOrchestrator.Loop.Stages;

public sealed record AssessResult(
    bool Relevant, double RelevanceScore, string RelevanceReason,
    string Verdict, string Rationale, string? CitedClause, TenderDetail TenderDetail);

/// <summary>
/// Stage 2/3 merged — Assess. Replaces the former separate ClassifyStage (Stage 2, relevance) and
/// VerifyStage (Stage 3, eligibility RAG) with one agentic session: Claude decides itself
/// whether/how to call get_tender/search_tenders — real MCP tools, exposed here as native
/// Anthropic tools via AnthropicClient.RunAgenticToolLoopAsync — to reach both a relevance
/// judgment and an eligibility verdict, rather than reasoning only over data pre-fetched
/// deterministically in code.
///
/// One deterministic exception, deliberately NOT left to the model: get_tender is still called
/// once in code up front, exactly as VerifyStage used to. This guarantees TenderDetail/
/// ProcurementMethod is always available for LoopRunner's mandatory ProcurementMethodPolicy
/// exclusion filter (a hard legal/business rule, not a reasoning judgment) and for the RAG
/// qualification-snippet lookup below, regardless of what Claude itself chooses to fetch during
/// the agentic phase. Claude still gets get_tender (and search_tenders) as callable tools for its
/// own research — free to re-fetch, or look at other tenders, or use neither.
///
/// Enforces the "eligible/ineligible must cite a clause" guardrail in code as well as in the
/// prompt (see AssessPolicy below), forcing "uncertain" if the model doesn't comply — carried
/// over unchanged in spirit from the former VerifyStage.
/// </summary>
public sealed class AssessStage(
    IMcpTenderClient mcpClient, IEligibilityIndex eligibilityIndex, AnthropicClient anthropicClient, ILogger<AssessStage> logger)
{
    private const int MaxTokens = 1024;
    private const int TopK = 5;
    private const int MaxToolIterations = 5;

    public Task<AssessResult> RunAsync(TenderSummary tender, CompanyProfileData companyProfile, CancellationToken cancellationToken) =>
        LoopTelemetry.TraceStageAsync("assess", tender.Id, async () =>
        {
            var detail = await mcpClient.GetTenderAsync(tender.Id, cancellationToken);

            // A blank eligibilityText is a real, observed case (not every Prozorro tender documents
            // one) — querying the index with empty input would throw at the embedding-API layer
            // (confirmed live: OpenAI.EmbeddingClient rejects an empty string outright), and even if
            // it didn't, there's nothing meaningful to search for. Left as an empty snippet list
            // rather than skipped entirely — the model still gets to reason about relevance either
            // way, and can fall back to "uncertain" itself for eligibility per the prompt's own rule.
            var snippets = string.IsNullOrWhiteSpace(detail.EligibilityText)
                ? []
                : await eligibilityIndex.QueryAsync(detail.EligibilityText, TopK, cancellationToken);

            var tools = await GetAllowedToolsAsync(cancellationToken);
            var initialMessage = BuildInitialMessage(tender, detail, companyProfile, snippets);

            using var agenticActivity = LoopTelemetry.StartLlmCallActivity(PromptBook.AssessVersion, initialMessage);
            var agentic = await anthropicClient.RunAgenticToolLoopAsync(
                PromptBook.AssessSystemPrompt, initialMessage, tools, ExecuteToolAsync, MaxTokens, MaxToolIterations, cancellationToken);
            LoopTelemetry.SetLlmOutput(agenticActivity, agentic.FinalText);

            var finalMessage = BuildFinalMessage(initialMessage, agentic);
            using var verdictActivity = LoopTelemetry.StartLlmCallActivity(PromptBook.AssessVersion, finalMessage);
            var result = await anthropicClient.CompleteStructuredAsync<AssessmentJsonResult>(
                PromptBook.AssessSystemPrompt, finalMessage, JsonSchemas.Assessment, MaxTokens, cancellationToken);
            LoopTelemetry.SetLlmOutput(verdictActivity, JsonSerializer.Serialize(result));

            var (verdict, citedClause, rationale) = AssessPolicy.EnforceCitedClauseGuardrail(
                result.EligibilityVerdict, result.CitedClause, result.EligibilityRationale);
            if (verdict != result.EligibilityVerdict)
            {
                logger.LogWarning(
                    "Assessor returned verdict '{Verdict}' without a citedClause for tender {TenderId} — forcing uncertain.",
                    result.EligibilityVerdict, tender.Id);
            }

            return new AssessResult(result.Relevant, result.RelevanceScore, result.RelevanceReason, verdict, rationale, citedClause, detail);
        });

    private async Task<IReadOnlyList<AnthropicTool>> GetAllowedToolsAsync(CancellationToken cancellationToken)
    {
        var available = await mcpClient.ListAvailableToolsAsync(cancellationToken);
        return available
            .Where(t => AssessPolicy.IsAllowedTool(t.Name))
            .Select(t => new AnthropicTool(t.Name, t.Description, t.InputSchema))
            .ToList();
    }

    /// <summary>Defense-in-depth: only ever actually calls a tool AssessPolicy.IsAllowedTool
    /// approves, even though those are the only names ever handed to Claude as available tools in
    /// the first place (GetAllowedToolsAsync above).</summary>
    private async Task<string> ExecuteToolAsync(string toolName, JsonElement input, CancellationToken cancellationToken)
    {
        if (!AssessPolicy.IsAllowedTool(toolName))
        {
            logger.LogWarning("Ignoring a tool call request for disallowed tool '{ToolName}'.", toolName);
            return $"Tool '{toolName}' is not available.";
        }

        var arguments = input.ValueKind == JsonValueKind.Object
            ? input.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value)
            : new Dictionary<string, object?>();

        try
        {
            return await mcpClient.CallToolRawAsync(toolName, arguments, cancellationToken);
        }
        catch (McpToolCallException ex)
        {
            // Handed back to Claude as a tool_result, not thrown — a failed lookup (e.g. a bogus
            // tenderId it guessed) is information the model can react to, not a reason to abort
            // the whole assessment.
            return $"Tool call failed: {ex.Message}";
        }
    }

    private static string BuildInitialMessage(
        TenderSummary tender, TenderDetail detail, CompanyProfileData companyProfile, IReadOnlyList<DocumentChunk> snippets) =>
        JsonSerializer.Serialize(new
        {
            tenderSummary = tender,
            tenderDetail = detail,
            companyProfile,
            qualificationSnippets = snippets.Select(s => s.Text),
        });

    private static string BuildFinalMessage(string initialMessage, AgenticResult agentic) =>
        JsonSerializer.Serialize(new
        {
            originalContext = JsonDocument.Parse(initialMessage).RootElement,
            researchFindings = agentic.FinalText,
            toolCallsMade = agentic.ToolCallsMade.Select(c => new { c.ToolName, c.Result }),
            instructions = "Now respond with your final assessment as strict JSON per the schema.",
        });

    private sealed record AssessmentJsonResult(
        [property: JsonPropertyName("relevant")] bool Relevant,
        [property: JsonPropertyName("relevanceScore")] double RelevanceScore,
        [property: JsonPropertyName("relevanceReason")] string RelevanceReason,
        [property: JsonPropertyName("eligibilityVerdict")] string EligibilityVerdict,
        [property: JsonPropertyName("eligibilityRationale")] string EligibilityRationale,
        [property: JsonPropertyName("citedClause")] string? CitedClause);
}

/// <summary>Pure citedClause-enforcement guardrail — same "pure logic, internal static, no I/O,
/// trivially unit-tested" pattern as Loop/Stages/HandoffStage.cs's HandoffPolicy. Forces verdict
/// to "uncertain" (dropping any citedClause and replacing the rationale) if the model returned
/// "eligible"/"ineligible" without a citedClause.</summary>
internal static class AssessPolicy
{
    // Only these two — list_tenders/get_company_profile stay deterministic, hardcoded call sites
    // elsewhere (DiscoverStage/LoopRunner respectively); see AssessStage's own doc comment for why.
    private static readonly HashSet<string> AllowedToolNames = ["get_tender", "search_tenders"];

    internal static (string Verdict, string? CitedClause, string Rationale) EnforceCitedClauseGuardrail(
        string verdict, string? citedClause, string rationale)
    {
        if (verdict is "eligible" or "ineligible" && string.IsNullOrWhiteSpace(citedClause))
        {
            return ("uncertain", null, $"Assessor omitted a required citedClause for verdict '{verdict}'.");
        }

        return (verdict, citedClause, rationale);
    }

    internal static bool IsAllowedTool(string toolName) => AllowedToolNames.Contains(toolName);
}
