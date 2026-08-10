namespace LoopOrchestrator.State;

/// <summary>
/// One proposed revision to a PromptBook.cs prompt, produced by Analysis/AnalysisRunner.cs.
/// Deliberately NOT a file/diff on disk — once deployed via `aspire deploy` the running container
/// has no writable source tree at all, and even if it did, an LLM-authored change to a production
/// prompt is exactly the "escalate rather than decide silently" case this whole project's
/// guardrails exist for. This record is the proposal's only artifact: a human reads it (via
/// GET /proposals or the Slack message it triggers), and if they agree, THEY manually paste
/// ProposedPromptText into PromptBook.cs as the next version — never applied automatically.
/// </summary>
public sealed record PromptProposalRecord(
    string ProposalId,
    DateTimeOffset CreatedAt,
    string TargetPrompt,          // "triage" | "verifier" | "handoff"
    string CurrentVersion,        // the PromptBook.*Version this was proposed against, e.g. "verifier v1"
    string ProposedPromptText,
    string Justification,
    IReadOnlyList<string> CitedTenderIds,
    string Status,                // Proposed | RejectedByGuardrail
    DateTimeOffset? SlackSentAt);

public static class PromptProposalStatus
{
    public const string Proposed = "Proposed";
    public const string RejectedByGuardrail = "RejectedByGuardrail";
}
