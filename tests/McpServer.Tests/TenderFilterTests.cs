using McpServer.Tenders;

namespace McpServer.Tests;

public class TenderFilterTests
{
    [Theory]
    [InlineData("active.tendering", true)]
    [InlineData("active.auction", true)]
    [InlineData("unsuccessful", false)]
    [InlineData("complete", false)]
    public void Apply_DefaultActiveStatus_MatchesOnlyActivePrefixedUpstreamStatuses(string upstreamStatus, bool expectedMatch)
    {
        var tenders = new[] { Make("1", status: upstreamStatus) };

        var result = TenderFilter.Apply(tenders, category: null, region: null, status: "active", limit: 20);

        Assert.Equal(expectedMatch, result.Count == 1);
    }

    [Fact]
    public void Apply_NonDefaultStatus_MatchesExactlyCaseInsensitive()
    {
        var tenders = new[] { Make("1", status: "Complete") };

        var result = TenderFilter.Apply(tenders, category: null, region: null, status: "complete", limit: 20);

        Assert.Single(result);
    }

    [Fact]
    public void Apply_FiltersByCategoryCaseInsensitiveSubstring()
    {
        var tenders = new[]
        {
            Make("1", cpvCategory: "Office Equipment"),
            Make("2", cpvCategory: "Medical Supplies"),
        };

        var result = TenderFilter.Apply(tenders, category: "medical", region: null, status: "active", limit: 20);

        Assert.Single(result);
        Assert.Equal("2", result[0].Id);
    }

    [Fact]
    public void Apply_FiltersByRegionCaseInsensitiveSubstring()
    {
        var tenders = new[]
        {
            Make("1", region: "Kyiv Oblast"),
            Make("2", region: "Lviv Oblast"),
        };

        var result = TenderFilter.Apply(tenders, category: null, region: "lviv", status: "active", limit: 20);

        Assert.Single(result);
        Assert.Equal("2", result[0].Id);
    }

    [Fact]
    public void Apply_ClampsLimitToMaxOf100()
    {
        var tenders = Enumerable.Range(0, 150).Select(i => Make(i.ToString())).ToArray();

        var result = TenderFilter.Apply(tenders, category: null, region: null, status: "active", limit: 500);

        Assert.Equal(100, result.Count);
    }

    [Fact]
    public void Apply_NonPositiveLimit_FallsBackToDefault()
    {
        var tenders = Enumerable.Range(0, 30).Select(i => Make(i.ToString())).ToArray();

        var result = TenderFilter.Apply(tenders, category: null, region: null, status: "active", limit: 0);

        Assert.Equal(20, result.Count);
    }

    [Fact]
    public void Search_RequiresAllKeywordsToMatch()
    {
        var tenders = new[]
        {
            Make("1", title: "Office chairs supply", cpvCategory: "Furniture"),
            Make("2", title: "Medical gloves supply", cpvCategory: "Medical"),
        };

        var result = TenderFilter.Search(tenders, "office supply", 20);

        Assert.Single(result);
        Assert.Equal("1", result[0].Id);
    }

    [Fact]
    public void Search_IsCaseInsensitiveAndMatchesTitleEnToo()
    {
        var tenders = new[] { Make("1", title: "Дорожні роботи", titleEn: "Road Construction") };

        var result = TenderFilter.Search(tenders, "ROAD", 20);

        Assert.Single(result);
    }

    [Fact]
    public void Search_ClampsLimit()
    {
        var tenders = Enumerable.Range(0, 10).Select(i => Make(i.ToString(), title: "Road works")).ToArray();

        var result = TenderFilter.Search(tenders, "road", 3);

        Assert.Equal(3, result.Count);
    }

    private static TenderSummary Make(
        string id,
        string title = "Title",
        string? titleEn = null,
        string? cpvCategory = null,
        string? region = null,
        string status = "active.tendering") =>
        new(
            Id: id,
            Title: title,
            TitleEn: titleEn,
            CpvCategory: cpvCategory,
            Value: new MoneyAmount(1000, "UAH"),
            ProcuringEntity: new ProcuringEntityInfo("Entity", region),
            TenderPeriod: new TenderPeriodInfo(null, null),
            Status: status,
            SourceUrl: $"https://example.test/{id}");
}
