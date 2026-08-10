using System.Text.Json;

namespace LoopOrchestrator.Llm;

/// <summary>
/// JSON Schemas for Anthropic's output_config.format (structured outputs) — makes Stage 2/3's
/// JSON shape schema-guaranteed by the API itself, rather than relying purely on the prompt's
/// "strict JSON only" instruction. AnthropicClient.CompleteStructuredAsync still retries on parse
/// failure as defense-in-depth per the prompt book's guardrail.
/// </summary>
public static class JsonSchemas
{
    /// <summary>
    /// Stage 2 (Assess) output: {relevant, relevanceScore, relevanceReason, eligibilityVerdict,
    /// eligibilityRationale, citedClause} — the combined relevance+eligibility verdict produced
    /// after AssessStage's agentic tool-use research phase concludes. relevanceScore has no
    /// "minimum"/"maximum" keyword — confirmed live against the real Anthropic API that
    /// output_config.format.schema rejects those on a "number" type ("properties maximum, minimum
    /// are not supported"), a real API-side constraint no amount of fake-HTTP unit testing could
    /// have caught. The 0.0-1.0 range is enforced by the prompt's own instruction instead
    /// (PromptBook.AssessSystemPrompt). citedClause is nullable — required whenever
    /// eligibilityVerdict is "eligible"/"ineligible", but "uncertain" (or a non-relevant tender)
    /// may legitimately have none; AnthropicClient.CompleteStructuredAsync's retry loop is the
    /// defense-in-depth backstop either way.
    /// </summary>
    public static readonly JsonElement Assessment = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "relevant": { "type": "boolean" },
            "relevanceScore": { "type": "number", "description": "A value between 0.0 and 1.0." },
            "relevanceReason": { "type": "string" },
            "eligibilityVerdict": { "type": "string", "enum": ["eligible", "ineligible", "uncertain"] },
            "eligibilityRationale": { "type": "string" },
            "citedClause": { "type": ["string", "null"] }
          },
          "required": ["relevant", "relevanceScore", "relevanceReason", "eligibilityVerdict", "eligibilityRationale", "citedClause"],
          "additionalProperties": false
        }
        """).RootElement.Clone();

    /// <summary>Stage 5 (Handoff) output: {categoryEmoji, shortTitle, description, rationale,
    /// keyQuestions}. The deterministic parts of the Slack Block Kit message (tender id, value,
    /// deadline, region, recommendation label/emoji) are assembled in HandoffStage from
    /// TenderDetail/the verdict directly, not generated here — see PromptBook.HandoffSystemPrompt's
    /// doc comment.</summary>
    public static readonly JsonElement HandoffBrief = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "categoryEmoji": { "type": "string" },
            "shortTitle": { "type": "string" },
            "description": { "type": "string" },
            "rationale": { "type": "string" },
            "keyQuestions": { "type": "array", "items": { "type": "string" } }
          },
          "required": ["categoryEmoji", "shortTitle", "description", "rationale", "keyQuestions"],
          "additionalProperties": false
        }
        """).RootElement.Clone();

    /// <summary>Stage 6 (self-improvement) output: {targetPrompt, proposedPromptText,
    /// justification, citedTenderIds}. See PromptBook.AnalysisSystemPrompt.</summary>
    public static readonly JsonElement PromptProposal = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "targetPrompt": { "type": "string", "enum": ["assess", "handoff"] },
            "proposedPromptText": { "type": "string" },
            "justification": { "type": "string" },
            "citedTenderIds": { "type": "array", "items": { "type": "string" } }
          },
          "required": ["targetPrompt", "proposedPromptText", "justification", "citedTenderIds"],
          "additionalProperties": false
        }
        """).RootElement.Clone();
}
