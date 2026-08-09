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
    /// Stage 2 (Classify) output: {relevant, relevanceScore, reason}. relevanceScore has no
    /// "minimum"/"maximum" keyword — confirmed live against the real Anthropic API that
    /// output_config.format.schema rejects those on a "number" type ("properties maximum, minimum
    /// are not supported"), a real API-side constraint no amount of fake-HTTP unit testing could
    /// have caught. The 0.0-1.0 range is enforced by the prompt's own instruction instead
    /// (PromptBook.TriageSystemPrompt); AnthropicClient.CompleteStructuredAsync's retry loop is the
    /// defense-in-depth backstop if the model ever drifts outside that range in a way that matters.
    /// </summary>
    public static readonly JsonElement TriageResult = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "relevant": { "type": "boolean" },
            "relevanceScore": { "type": "number", "description": "A value between 0.0 and 1.0." },
            "reason": { "type": "string" }
          },
          "required": ["relevant", "relevanceScore", "reason"],
          "additionalProperties": false
        }
        """).RootElement.Clone();

    /// <summary>Stage 3 (Verify) output: {verdict, rationale, citedClause}. citedClause is
    /// nullable — the verifier prompt requires "eligible"/"ineligible" to always carry one, but
    /// "uncertain" may legitimately have none.</summary>
    public static readonly JsonElement EligibilityVerdict = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "verdict": { "type": "string", "enum": ["eligible", "ineligible", "uncertain"] },
            "rationale": { "type": "string" },
            "citedClause": { "type": ["string", "null"] }
          },
          "required": ["verdict", "rationale", "citedClause"],
          "additionalProperties": false
        }
        """).RootElement.Clone();
}
