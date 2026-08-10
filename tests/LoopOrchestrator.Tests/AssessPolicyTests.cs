using LoopOrchestrator.Loop.Stages;

namespace LoopOrchestrator.Tests;

/// <summary>Carried over unchanged in spirit from the former VerifyStage's inline citedClause
/// enforcement (see AssessStage.cs's doc comment) — now a pure, directly-testable function.</summary>
public class AssessPolicyTests
{
    [Theory]
    [InlineData("eligible")]
    [InlineData("ineligible")]
    public void MissingCitedClause_ForcesUncertain(string verdict)
    {
        var (resultVerdict, citedClause, rationale) =
            AssessPolicy.EnforceCitedClauseGuardrail(verdict, citedClause: null, rationale: "original rationale");

        Assert.Equal("uncertain", resultVerdict);
        Assert.Null(citedClause);
        Assert.Contains(verdict, rationale);
    }

    [Theory]
    [InlineData("eligible")]
    [InlineData("ineligible")]
    public void BlankCitedClause_ForcesUncertain(string verdict)
    {
        var (resultVerdict, citedClause, _) =
            AssessPolicy.EnforceCitedClauseGuardrail(verdict, citedClause: "   ", rationale: "original rationale");

        Assert.Equal("uncertain", resultVerdict);
        Assert.Null(citedClause);
    }

    [Theory]
    [InlineData("eligible")]
    [InlineData("ineligible")]
    public void WithCitedClause_PassesThroughUnchanged(string verdict)
    {
        var (resultVerdict, citedClause, rationale) =
            AssessPolicy.EnforceCitedClauseGuardrail(verdict, citedClause: "a literal excerpt", rationale: "original rationale");

        Assert.Equal(verdict, resultVerdict);
        Assert.Equal("a literal excerpt", citedClause);
        Assert.Equal("original rationale", rationale);
    }

    [Fact]
    public void Uncertain_PassesThroughUnchanged_EvenWithoutCitedClause()
    {
        var (resultVerdict, citedClause, rationale) =
            AssessPolicy.EnforceCitedClauseGuardrail("uncertain", citedClause: null, rationale: "original rationale");

        Assert.Equal("uncertain", resultVerdict);
        Assert.Null(citedClause);
        Assert.Equal("original rationale", rationale);
    }

    [Theory]
    [InlineData("get_tender")]
    [InlineData("search_tenders")]
    public void IsAllowedTool_TrueForTheCuratedTwo(string toolName)
    {
        Assert.True(AssessPolicy.IsAllowedTool(toolName));
    }

    [Theory]
    [InlineData("list_tenders")] // Discover's job — must stay deterministic, never LLM-driven
    [InlineData("get_company_profile")] // pre-fetched once per run by LoopRunner, not per-tender
    [InlineData("delete_everything")] // not a real tool at all
    [InlineData("")]
    public void IsAllowedTool_FalseForAnythingElse(string toolName)
    {
        Assert.False(AssessPolicy.IsAllowedTool(toolName));
    }
}
