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
    public void AssessProposal_MissingCitedClauseRequirement_IsRejected()
    {
        const string proposal = """
            You are assessing relevance and eligibility. Decide eligible/ineligible/uncertain based
            on eligibilityText. Never invent a requirement that is not present in eligibilityText.
            Do not guess at eligibility requirements when scoring relevance.
            Respond with strict JSON only: {"eligibilityVerdict": "...", "citedClause": null}
            """; // dropped the literal "citedClause"-must-be-cited requirement's wording

        Assert.False(PromptGuardrails.IsSafe("assess", proposal));
    }

    [Fact]
    public void AssessProposal_MissingNeverInventRequirement_IsRejected()
    {
        const string proposal = """
            You are assessing relevance and eligibility. Every "eligible"/"ineligible" verdict must
            include citedClause. If unclear, return "uncertain".
            Do not guess at eligibility requirements when scoring relevance.
            """; // dropped "Never invent a requirement"

        Assert.False(PromptGuardrails.IsSafe("assess", proposal));
    }

    [Fact]
    public void AssessProposal_MissingUncertainFallback_IsRejected()
    {
        const string proposal = """
            You are assessing relevance and eligibility. Every "eligible"/"ineligible" verdict must
            include citedClause. Never invent a requirement that is not present in eligibilityText.
            Do not guess at eligibility requirements when scoring relevance.
            """; // dropped the "uncertain" fallback requirement entirely

        Assert.False(PromptGuardrails.IsSafe("assess", proposal));
    }

    [Fact]
    public void AssessProposal_MissingDoNotGuessAtEligibilityRequirement_IsRejected()
    {
        const string proposal = """
            You are assessing relevance and eligibility. Every "eligible"/"ineligible" verdict must
            include citedClause. Never invent a requirement that is not present in eligibilityText.
            If unclear, return "uncertain".
            """; // dropped the relevance-stage "Do not guess at eligibility requirements" phrase
             // that used to belong to the separate "triage" prompt before the assess merge

        Assert.False(PromptGuardrails.IsSafe("assess", proposal));
    }

    [Fact]
    public void AssessProposal_KeepingAllRequiredPhrases_IsSafe()
    {
        const string proposal = """
            You are assessing relevance and eligibility, tightened for construction-adjacent
            tenders. Do not guess at eligibility requirements when scoring relevance.
            Every "eligible"/"ineligible" verdict must include citedClause: a literal excerpt.
            Never invent a requirement that is not present in eligibilityText.
            If unclear, return eligibilityVerdict "uncertain".
            """; // reworded around the edges, but every required phrase survives verbatim, each
             // on its own line so a line-wrap in this test literal can't itself break the match

        Assert.True(PromptGuardrails.IsSafe("assess", proposal));
    }

    [Fact]
    public void HandoffProposal_MissingItsRequiredPhrase_IsRejected()
    {
        Assert.False(PromptGuardrails.IsSafe("handoff", "Some rewritten handoff prompt that always sounds confident."));
    }

    [Fact]
    public void UnknownTargetPrompt_IsNotSafe()
    {
        // No protected-phrase entry for a prompt name that isn't one of the two real ones —
        // fail closed, not open.
        Assert.False(PromptGuardrails.IsSafe("some-made-up-prompt", "anything at all"));
    }
}
