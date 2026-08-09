using LoopOrchestrator.Loop.Stages;
using LoopOrchestrator.Mcp;

namespace LoopOrchestrator.Tests;

/// <summary>
/// Proves the state store's seen-ID exclusion is the SOLE authoritative idempotency guard in
/// DiscoverStage.FilterNewTenders — the date-based value (lastRunAt) is accepted only for
/// informational logging and must never cause a genuinely new, unseen tender to be dropped.
/// </summary>
public class IdempotencyFilterTests
{
    [Fact]
    public void FilterNewTenders_ExcludesTendersAlreadyInSeenSet()
    {
        var seen = new HashSet<string> { "1" };
        var candidates = new[] { Tender("1"), Tender("2") };

        var result = DiscoverStage.FilterNewTenders(candidates, seen, lastRunAt: null);

        Assert.Equal(["2"], result.Select(t => t.Id));
    }

    [Fact]
    public void FilterNewTenders_IncludesUnseenTender_EvenWhenStartDatePredatesLastRun()
    {
        var lastRunAt = DateTimeOffset.UtcNow;
        var oldButUnseen = Tender("old-unseen", startDate: lastRunAt.AddDays(-30));
        var seen = new HashSet<string>(); // nothing seen yet

        var result = DiscoverStage.FilterNewTenders([oldButUnseen], seen, lastRunAt);

        Assert.Equal(["old-unseen"], result.Select(t => t.Id));
    }

    [Fact]
    public void FilterNewTenders_IncludesUnseenTender_WithNullStartDate()
    {
        var lastRunAt = DateTimeOffset.UtcNow;
        var noStartDate = Tender("no-date", startDate: null);

        var result = DiscoverStage.FilterNewTenders([noStartDate], [], lastRunAt);

        Assert.Equal(["no-date"], result.Select(t => t.Id));
    }

    [Fact]
    public void FilterNewTenders_ReturnsEmpty_WhenAllCandidatesAlreadySeen()
    {
        var candidates = new[] { Tender("1"), Tender("2") };
        var seen = new HashSet<string> { "1", "2" };

        var result = DiscoverStage.FilterNewTenders(candidates, seen, lastRunAt: null);

        Assert.Empty(result);
    }

    private static TenderSummary Tender(string id, DateTimeOffset? startDate = null) => new(
        Id: id,
        Title: "Test tender " + id,
        TitleEn: null,
        CpvCategory: "45000000",
        Value: new MoneyAmount(100_000m, "UAH"),
        ProcuringEntity: new ProcuringEntityInfo("Test Entity", "Kyiv"),
        TenderPeriod: new TenderPeriodInfo(startDate, EndDate: null),
        Status: "active",
        SourceUrl: "https://prozorro.gov.ua/tender/" + id);
}
