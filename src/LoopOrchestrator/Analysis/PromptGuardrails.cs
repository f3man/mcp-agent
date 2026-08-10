namespace LoopOrchestrator.Analysis;

/// <summary>
/// First-line, deterministic defense against a self-improvement proposal weakening this project's
/// own safety guarantees — mirrors Loop/Stages/HandoffStage.cs's HandoffPolicy in spirit: pure, no
/// I/O, trivially unit-tested. Checked immediately after the LLM returns a proposal
/// (AnalysisRunner.cs), before it's ever Slacked to a human.
///
/// A required-substring check is a cheap, honestly gameable defense — wording could keep a phrase
/// present while changing the surrounding meaning around it. It is deliberately paired with, not a
/// substitute for, the human review gate (AnalysisRunner never applies a proposal itself — see
/// PromptProposalRecord's doc comment). A stronger, behavioral version (fixtures that assert
/// actual model behavior against a candidate prompt, not just text presence) is deferred — see
/// docs/conclusions-2nd-iteration.md's Phase 2 notes.
/// </summary>
public static class PromptGuardrails
{
    // One entry per prompt PromptBook.cs currently defines that Stage 6 is allowed to propose
    // changes to. Each required phrase is copied verbatim from the live prompt text — if a
    // proposal's full replacement text no longer contains ALL of them, it's rejected outright.
    // "assess" carries the union of what used to be two separate entries (the former "verifier"
    // and "triage" prompts, merged into one — see PromptBook.AssessSystemPrompt's doc comment).
    private static readonly Dictionary<string, string[]> ProtectedPhrases = new()
    {
        ["assess"] = ["citedClause", "Never invent a requirement", "\"uncertain\"", "Do not guess at eligibility requirements"],
        ["handoff"] = ["Never state a recommendation more confidently than"],
    };

    public static bool IsSafe(string targetPrompt, string proposedPromptText) =>
        ProtectedPhrases.TryGetValue(targetPrompt, out var required)
        && required.All(proposedPromptText.Contains);
}
