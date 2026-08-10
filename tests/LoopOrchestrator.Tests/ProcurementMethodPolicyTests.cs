using LoopOrchestrator.Loop.Stages;

namespace LoopOrchestrator.Tests;

public class ProcurementMethodPolicyTests
{
    [Theory]
    [InlineData("limited")]
    [InlineData("Limited")]
    [InlineData("LIMITED")]
    public void IsExcluded_MatchesLimited_CaseInsensitively(string procurementMethod)
    {
        Assert.True(ProcurementMethodPolicy.IsExcluded(procurementMethod));
    }

    [Theory]
    [InlineData("open")]
    [InlineData("selective")]
    [InlineData(null)]
    [InlineData("")]
    public void IsExcluded_False_ForEverythingElse(string? procurementMethod)
    {
        Assert.False(ProcurementMethodPolicy.IsExcluded(procurementMethod));
    }
}
