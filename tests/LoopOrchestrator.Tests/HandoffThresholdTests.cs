using LoopOrchestrator.Loop.Stages;
using LoopOrchestrator.Mcp;

namespace LoopOrchestrator.Tests;

public class HandoffThresholdTests
{
    [Theory]
    [InlineData("uncertain", null, 500_000)]
    [InlineData("uncertain", 1.0, 500_000)] // uncertain escalates regardless of value
    [InlineData("uncertain", 10_000_000.0, 500_000)]
    public void Uncertain_AlwaysEscalates(string verdict, double? amount, decimal threshold)
    {
        var value = amount is null ? null : new MoneyAmount((decimal)amount.Value, "UAH");
        Assert.True(HandoffPolicy.ShouldEscalate(verdict, value, threshold));
    }

    [Fact]
    public void Eligible_OverThreshold_Escalates()
    {
        var value = new MoneyAmount(600_000m, "UAH");
        Assert.True(HandoffPolicy.ShouldEscalate("eligible", value, handoffValueThreshold: 500_000m));
    }

    [Fact]
    public void Eligible_AtThreshold_DoesNotEscalate()
    {
        var value = new MoneyAmount(500_000m, "UAH");
        Assert.False(HandoffPolicy.ShouldEscalate("eligible", value, handoffValueThreshold: 500_000m));
    }

    [Fact]
    public void Eligible_UnderThreshold_DoesNotEscalate()
    {
        var value = new MoneyAmount(100_000m, "UAH");
        Assert.False(HandoffPolicy.ShouldEscalate("eligible", value, handoffValueThreshold: 500_000m));
    }

    [Fact]
    public void Eligible_WithNullValue_DoesNotEscalate()
    {
        Assert.False(HandoffPolicy.ShouldEscalate("eligible", value: null, handoffValueThreshold: 500_000m));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(10_000_000.0)]
    public void Ineligible_NeverEscalates_RegardlessOfValue(double? amount)
    {
        var value = amount is null ? null : new MoneyAmount((decimal)amount.Value, "UAH");
        Assert.False(HandoffPolicy.ShouldEscalate("ineligible", value, handoffValueThreshold: 500_000m));
    }
}
