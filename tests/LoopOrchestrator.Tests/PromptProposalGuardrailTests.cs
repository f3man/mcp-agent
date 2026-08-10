using LoopOrchestrator.Analysis;

namespace LoopOrchestrator.Tests;

/// <summary>
/// The load-bearing safety test for the self-improvement outer loop: a proposal that would strip
/// a required guardrail phrase from its target prompt must be rejected automatically, before any
/// human ever sees it (see Analysis/PromptGuardrails.cs's doc comment on why this is a
/// first-line, not sufficient-on-its-own, defense).
/// </summary>
public class PromptProposalGuardrailTests
{
    [Fact]
    public void VerifierProposal_MissingCitedClauseRequirement_IsRejected()
    {
        const string proposal = """
            You are an eligibility verifier. Decide eligible/ineligible/uncertain based on
            eligibilityText. Never invent a requirement that is not present in eligibilityText.
            Respond with strict JSON only: {"verdict": "...", "rationale": "...", "citedClause": null}
            """; // dropped the literal "citedClause"-must-be-cited requirement's wording

        Assert.False(PromptGuardrails.IsSafe("verifier", proposal));
    }

    [Fact]
    public void VerifierProposal_MissingNeverInventRequirement_IsRejected()
    {
        const string proposal = """
            You are an eligibility verifier. Every "eligible"/"ineligible" verdict must include
            citedClause. If unclear, return verdict "uncertain".
            """; // dropped "Never invent a requirement"

        Assert.False(PromptGuardrails.IsSafe("verifier", proposal));
    }

    [Fact]
    public void VerifierProposal_MissingUncertainFallback_IsRejected()
    {
        const string proposal = """
            You are an eligibility verifier. Every "eligible"/"ineligible" verdict must include
            citedClause. Never invent a requirement that is not present in eligibilityText.
            """; // dropped the "uncertain" fallback requirement entirely

        Assert.False(PromptGuardrails.IsSafe("verifier", proposal));
    }

    [Fact]
    public void VerifierProposal_KeepingAllRequiredPhrases_IsSafe()
    {
        const string proposal = """
            You are an eligibility verifier, tightened for construction-adjacent tenders. Every
            "eligible"/"ineligible" verdict must include citedClause: a literal excerpt.
            Never invent a requirement that is not present in eligibilityText.
            If unclear, return verdict "uncertain".
            """; // reworded around the edges, but every required phrase survives verbatim, each
             // on its own line so a line-wrap in this test literal can't itself break the match

        Assert.True(PromptGuardrails.IsSafe("verifier", proposal));
    }

    [Theory]
    [InlineData("triage", "Some rewritten triage prompt with no mention of guessing at all.")]
    [InlineData("handoff", "Some rewritten handoff prompt that always sounds confident.")]
    public void Proposal_MissingItsPromptsRequiredPhrase_IsRejected(string targetPrompt, string proposal)
    {
        Assert.False(PromptGuardrails.IsSafe(targetPrompt, proposal));
    }

    [Fact]
    public void UnknownTargetPrompt_IsNotSafe()
    {
        // No protected-phrase entry for a prompt name that isn't one of the three real ones —
        // fail closed, not open.
        Assert.False(PromptGuardrails.IsSafe("some-made-up-prompt", "anything at all"));
    }
}
