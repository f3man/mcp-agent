using McpServer.Tenders;
using McpServer.Tenders.Prozorro;

namespace McpServer.Tests;

public class ProzorroMapperTests
{
    [Fact]
    public void ToSummary_MapsUpstreamFields_IncludingSnakeCaseTitleEnAndCpvClassification()
    {
        var tender = MakeTender(
            titleEn: "Office supplies delivery",
            value: new ProzorroValue(150000m, "UAH"),
            procuringEntity: new ProzorroProcuringEntity("Kyiv City Council", new ProzorroAddress("Kyiv")),
            items: [new ProzorroItem(new ProzorroClassification("30192000-1", "Office supplies"))],
            tenderNumber: "UA-2026-01-01-000001-a");

        var summary = ProzorroMapper.ToSummary(tender);

        Assert.Equal(tender.Id, summary.Id);
        Assert.Equal("Office supplies delivery", summary.TitleEn);
        Assert.Equal("Office supplies", summary.CpvCategory);
        Assert.Equal(150000m, summary.Value!.Amount);
        Assert.Equal("UAH", summary.Value.Currency);
        Assert.Equal("Kyiv City Council", summary.ProcuringEntity.Name);
        Assert.Equal("Kyiv", summary.ProcuringEntity.Region);
        Assert.Equal("https://prozorro.gov.ua/tender/UA-2026-01-01-000001-a", summary.SourceUrl);
    }

    [Fact]
    public void ToSummary_FallsBackToInternalId_WhenTenderNumberMissing()
    {
        var tender = MakeTender(tenderNumber: null);

        var summary = ProzorroMapper.ToSummary(tender);

        Assert.Equal($"https://prozorro.gov.ua/tender/{tender.Id}", summary.SourceUrl);
    }

    [Fact]
    public void ToSummary_CpvCategory_FallsBackToClassificationId_WhenDescriptionMissing()
    {
        var tender = MakeTender(items: [new ProzorroItem(new ProzorroClassification("30192000-1", null))]);

        var summary = ProzorroMapper.ToSummary(tender);

        Assert.Equal("30192000-1", summary.CpvCategory);
    }

    [Fact]
    public void ToSummary_CpvCategory_IsNull_WhenNoItems()
    {
        var tender = MakeTender(items: null);

        var summary = ProzorroMapper.ToSummary(tender);

        Assert.Null(summary.CpvCategory);
    }

    [Fact]
    public void ToDetail_UsesEligibilityCriteria_WhenPresent()
    {
        var tender = MakeTender(eligibilityCriteria: "Must hold ISO 9001.", description: "General requirements apply.");

        var detail = ProzorroMapper.ToDetail(tender);

        Assert.Equal("Must hold ISO 9001.", detail.EligibilityText);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ToDetail_FallsBackToDescription_WhenEligibilityCriteriaMissingOrBlank(string? eligibilityCriteria)
    {
        var tender = MakeTender(eligibilityCriteria: eligibilityCriteria, description: "General requirements apply.");

        var detail = ProzorroMapper.ToDetail(tender);

        Assert.Equal("General requirements apply.", detail.EligibilityText);
    }

    [Fact]
    public void ToDetail_EligibilityText_IsEmpty_WhenNeitherFieldIsPresent()
    {
        var tender = MakeTender(eligibilityCriteria: null, description: null);

        var detail = ProzorroMapper.ToDetail(tender);

        Assert.Equal(string.Empty, detail.EligibilityText);
    }

    private static ProzorroTender MakeTender(
        string id = "id-1",
        string title = "Title",
        string? titleEn = null,
        string? description = "desc",
        string? eligibilityCriteria = null,
        ProzorroValue? value = null,
        ProzorroProcuringEntity? procuringEntity = null,
        ProzorroPeriod? tenderPeriod = null,
        string status = "active.tendering",
        List<ProzorroItem>? items = null,
        string? tenderNumber = "UA-1") =>
        new(
            Id: id,
            Title: title,
            TitleEn: titleEn,
            Description: description,
            EligibilityCriteria: eligibilityCriteria,
            Value: value,
            ProcuringEntity: procuringEntity,
            TenderPeriod: tenderPeriod,
            Status: status,
            Items: items,
            TenderNumber: tenderNumber);
}
